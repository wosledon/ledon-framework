using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Data;

/// <summary>
/// 审计拦截器：<c>SaveChanges</c> 时自动填充 <see cref="IAudited"/> 的时间戳与操作人，
/// 并把 <see cref="ISoftDelete"/> 实体的删除转换为软删除更新。
/// 业务代码不再手写 CreatedAt/ModifiedAt/软删除判断——实现标记接口即生效
/// （<c>AddLedonData(o =&gt; o.EnableAuditing = true)</c> 时随容器自动注册）。
/// </summary>
internal sealed class LedonAuditInterceptor : SaveChangesInterceptor
{
    private readonly IAuditUserAccessor? _userAccessor;
    private readonly LedonDataOptions _options;
    private readonly ILogger<LedonAuditInterceptor> _logger;

    public LedonAuditInterceptor(
        LedonDataOptions options,
        ILogger<LedonAuditInterceptor> logger,
        IAuditUserAccessor? userAccessor = null)
    {
        _options = options;
        _logger = logger;
        _userAccessor = userAccessor;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
            return;

        var now = DateTime.UtcNow;
        var user = _options.EnableAuditing ? _userAccessor?.CurrentUserId : null;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added when entry.Entity is IAudited audited && _options.EnableAuditing:
                    audited.CreatedAt = now;
                    audited.CreatedBy = user;
                    audited.ModifiedAt = now;
                    audited.ModifiedBy = user;
                    break;

                case EntityState.Modified when entry.Entity is IAudited audited && _options.EnableAuditing:
                    audited.ModifiedAt = now;
                    audited.ModifiedBy = user;
                    break;

                // 软删除：删除 → 更新（IsDeleted = true + 审计时间戳），行不出库
                case EntityState.Deleted when entry.Entity is ISoftDelete softDelete && _options.AutoSoftDelete:
                    entry.State = EntityState.Modified;
                    softDelete.IsDeleted = true;

                    if (entry.Entity is IAudited auditedByDelete && _options.EnableAuditing)
                    {
                        auditedByDelete.ModifiedAt = now;
                        auditedByDelete.ModifiedBy = user;
                    }

                    _logger.LogDebug("软删除转换：{Entity} 的删除操作已转换为更新", entry.Entity.GetType().Name);
                    break;
            }
        }
    }
}
