using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ledon.AuditLog;

/// <summary>
/// 审计拦截器：<b>提交前</b>从 ChangeTracker 采集 Added/Modified/Deleted（此时状态还有效），
/// <b>提交成功后</b>再经 <see cref="IAuditStore"/> 落库。
/// <list type="bullet">
///   <item>审计在业务事务提交后写：业务回滚不会留下"改过了"的假记录；</item>
///   <item>审计写入自身失败只记日志，不回滚业务（审计不能拖垮业务）；</item>
///   <item>审计实体自身不进流水。</item>
/// </list>
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    // 拦截器是单例、上下文是瞬态的：按上下文实例暂存"待落库"的采集结果
    private readonly ConcurrentDictionary<DbContext, List<AuditEntry>> _pending = new();
    private readonly IAuditStore _store;
    private readonly LedonAuditOptions _options;
    private readonly ILogger<AuditSaveChangesInterceptor> _logger;

    public AuditSaveChangesInterceptor(
        IAuditStore store,
        LedonAuditOptions options,
        ILogger<AuditSaveChangesInterceptor> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>提交前：采集本次变更（异步路径）。</summary>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>提交前：采集本次变更（同步路径）。</summary>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <summary>提交成功后：把采集到的审计流水落库。</summary>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await FlushAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>提交成功后：把采集到的审计流水落库（同步路径）。</summary>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    /// <summary>提交失败：丢弃本次采集，不留假记录。</summary>
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        // 业务保存失败：丢弃本次采集（不留假记录）
        if (eventData.Context is not null)
            _pending.TryRemove(eventData.Context, out _);

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
            return;

        try
        {
            var entries = new List<AuditEntry>();

            foreach (var tracked in context.ChangeTracker.Entries())
            {
                if (tracked.Entity is AuditEntry || tracked.Metadata.IsOwned())
                    continue;

                var action = tracked.State switch
                {
                    EntityState.Added => (AuditAction?)AuditAction.Created,
                    EntityState.Modified => AuditAction.Updated,
                    EntityState.Deleted => AuditAction.Deleted,
                    _ => null,
                };

                if (action is null)
                    continue;

                // 按选项过滤动作（默认全记）
                if (action == AuditAction.Created && !_options.AuditCreated)
                    continue;

                if (action == AuditAction.Updated && !_options.AuditUpdated)
                    continue;

                if (action == AuditAction.Deleted && !_options.AuditDeleted)
                    continue;

                entries.Add(new AuditEntry
                {
                    EntityType = tracked.Metadata.ClrType.Name,
                    EntityId = KeyOf(tracked),
                    Action = action.Value,
                    Changes = ChangesOf(tracked, action.Value),
                    UserId = AuditContext.UserId,
                    UserName = AuditContext.UserName,
                    RequestId = AuditContext.RequestId,
                    IpAddress = AuditContext.IpAddress,
                });
            }

            if (entries.Count > 0)
                _pending[context] = entries;
        }
        catch (Exception ex)
        {
            // 采集失败（如实体无主键）不影响业务保存
            _logger.LogWarning(ex, "审计采集失败（已跳过本次）");
        }
    }

    private async Task FlushAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || !_pending.TryRemove(context, out var entries))
            return;

        foreach (var entry in entries)
        {
            try
            {
                await _store.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "审计写入失败（{Entity} {EntityId}）", entry.EntityType, entry.EntityId);
            }
        }

        _logger.LogDebug("已记录 {Count} 条审计流水", entries.Count);
    }

    private static string KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
            return "";

        return string.Join(",", key.Properties
            .Select(property => entry.Property(property.Name).CurrentValue?.ToString() ?? ""));
    }

    /// <summary>Updated 只记真实变化的属性；Created 记全部当前值；Deleted 只记键。</summary>
    private static string? ChangesOf(EntityEntry entry, AuditAction action)
    {
        if (action == AuditAction.Created)
        {
            var created = entry.Properties
                .Where(property => !property.Metadata.IsShadowProperty() || property.CurrentValue is not null)
                .Select(property => new PropertyChange(property.Metadata.Name, null, property.CurrentValue));

            return AuditJson.Serialize(created);
        }

        if (action == AuditAction.Updated)
        {
            var changed = entry.Properties
                .Where(property => property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                .Select(property => new PropertyChange(property.Metadata.Name, property.OriginalValue, property.CurrentValue))
                .ToArray();

            // 只有审计字段变化时不算业务变更
            return changed.Length == 0 ? null : AuditJson.Serialize(changed);
        }

        return null;
    }
}
