using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ledon.Workflow;

/// <summary>工作流实例的持久化实体（EF Core 映射）。</summary>
public class WorkflowInstanceEntity
{
    public Guid Id { get; set; }

    public string WorkflowName { get; set; } = string.Empty;

    public string? CurrentStep { get; set; }

    public WorkflowStatus Status { get; set; }

    public int RetryCount { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>更新时间（UTC 的 DateTime）——SQLite 等提供程序不支持 DateTimeOffset 排序，用这列。</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>已完成步骤（JSON 数组，保序）。</summary>
    public string CompletedStepsJson { get; set; } = "[]";

    /// <summary>实例数据（JSON 对象）。</summary>
    public string DataJson { get; set; } = "{}";

    /// <summary>
    /// 乐观并发令牌：映射为并发令牌（EF 会把它并进 UPDATE 的 WHERE）。
    /// 两个节点同时保存同一实例时，后提交的那个影响 0 行 → 抛
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>，
    /// 而不是静默覆盖对方刚写的 CompletedSteps。
    /// </summary>
    public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>当前持有租约的节点。</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>租约到期时间（UTC 的 DateTime；SQLite 不支持 DateTimeOffset 排序/比较）。</summary>
    public DateTime? ClaimedUntilUtc { get; set; }
}

/// <summary>
/// 带工作流存储的 DbContext 基类：用户的 DbContext 继承它即获得实例表
/// （与 <c>OutboxDbContext</c> 同一风格——继承即获得表结构约定）。
/// </summary>
public abstract class WorkflowDbContext : DbContext
{
    protected WorkflowDbContext()
    {
    }

    protected WorkflowDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<WorkflowInstanceEntity> WorkflowInstances => Set<WorkflowInstanceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowInstanceEntity>(entity =>
        {
            entity.HasKey(instance => instance.Id);
            entity.Property(instance => instance.WorkflowName).IsRequired().HasMaxLength(200);
            entity.Property(instance => instance.CurrentStep).HasMaxLength(200);
            entity.Property(instance => instance.CompletedStepsJson).IsRequired();
            entity.Property(instance => instance.DataJson).IsRequired();

            // 乐观并发令牌：EF 会把它并进 UPDATE/DELETE 的 WHERE，
            // 影响 0 行即抛 DbUpdateConcurrencyException（丢更新变显式失败）
            entity.Property(instance => instance.ConcurrencyToken).IsConcurrencyToken();

            // 恢复查询按状态过滤 + 租约过滤 + 更新时间排序
            // （UpdatedAtUtc / ClaimedUntilUtc：SQLite 不支持 DateTimeOffset 比较）
            entity.HasIndex(instance => instance.Status);
            entity.HasIndex(instance => instance.UpdatedAtUtc);
            entity.HasIndex(instance => instance.ClaimedUntilUtc);
        });

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>EF 实现的工作流存储（数据以 JSON 列保存，与实例一一对应）。</summary>
internal sealed class EfWorkflowStore(WorkflowDbContext context) : IWorkflowStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public async Task SaveAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var completed = JsonSerializer.Serialize(instance.CompletedSteps, SerializerOptions);
        var data = JsonSerializer.Serialize(
            instance.Data.ToDictionary(pair => pair.Key, pair => pair.Value), SerializerOptions);
        var now = DateTime.UtcNow;
        var nextToken = Guid.NewGuid().ToString("N");

        // 条件更新：WHERE Id = @id AND ConcurrencyToken = @调用方持有的旧值。
        // 别人改过同一行时旧值就对不上 → 影响 0 行 → 抛并发异常（而不是静默覆盖）
        var updated = await context.WorkflowInstances
            .Where(entity => entity.Id == instance.Id && entity.ConcurrencyToken == instance.ConcurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.WorkflowName, instance.WorkflowName)
                .SetProperty(entity => entity.CurrentStep, instance.CurrentStep)
                .SetProperty(entity => entity.Status, instance.Status)
                .SetProperty(entity => entity.RetryCount, instance.RetryCount)
                .SetProperty(entity => entity.LastError, instance.LastError)
                .SetProperty(entity => entity.CreatedAt, instance.CreatedAt)
                .SetProperty(entity => entity.UpdatedAt, DateTimeOffset.UtcNow)
                .SetProperty(entity => entity.UpdatedAtUtc, now)
                .SetProperty(entity => entity.CompletedStepsJson, completed)
                .SetProperty(entity => entity.DataJson, data)
                .SetProperty(entity => entity.ClaimedBy, instance.ClaimedBy)
                .SetProperty(entity => entity.ClaimedUntilUtc, instance.ClaimedUntilUtc?.UtcDateTime)
                .SetProperty(entity => entity.ConcurrencyToken, nextToken),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated > 0)
        {
            instance.ConcurrencyToken = nextToken;
            return;
        }

        var exists = await context.WorkflowInstances
            .AnyAsync(entity => entity.Id == instance.Id, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
            throw new DbUpdateConcurrencyException(
                $"工作流实例 {instance.Id} 已被其他节点修改（并发冲突），本次保存被拒绝");

        // 首次保存：直接插入
        context.WorkflowInstances.Add(new WorkflowInstanceEntity
        {
            Id = instance.Id,
            WorkflowName = instance.WorkflowName,
            CurrentStep = instance.CurrentStep,
            Status = instance.Status,
            RetryCount = instance.RetryCount,
            LastError = instance.LastError,
            CreatedAt = instance.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedAtUtc = now,
            CompletedStepsJson = completed,
            DataJson = data,
            ConcurrencyToken = nextToken,
            ClaimedBy = instance.ClaimedBy,
            ClaimedUntilUtc = instance.ClaimedUntilUtc?.UtcDateTime,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        instance.ConcurrencyToken = nextToken;
    }

    public async Task<WorkflowInstance?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.WorkflowInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToInstance(entity);
    }

    public async Task<IReadOnlyList<WorkflowInstance>> GetResumableAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // 只取"没人认领"或"租约已过期"的：别的节点正在跑的实例不重复捞
        return await context.WorkflowInstances
            .AsNoTracking()
            .Where(instance => instance.Status == WorkflowStatus.Running
                && (instance.ClaimedUntilUtc == null || instance.ClaimedUntilUtc < now))
            .OrderBy(instance => instance.UpdatedAtUtc)
            .Take(batchSize)
            .Select(entity => ToInstance(entity))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ClaimAsync(
        Guid instanceId,
        string owner,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(lease);

        // 原子抢占：只把"未认领或租约已过期"的行改成自己的。
        // 两个节点同时认领时只有一个能影响 1 行
        var claimed = await context.WorkflowInstances
            .Where(instance => instance.Id == instanceId
                && (instance.ClaimedUntilUtc == null
                    || instance.ClaimedUntilUtc < now
                    || instance.ClaimedBy == owner))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(instance => instance.ClaimedBy, owner)
                .SetProperty(instance => instance.ClaimedUntilUtc, until)
                .SetProperty(instance => instance.UpdatedAtUtc, now)
                .SetProperty(instance => instance.UpdatedAt, DateTimeOffset.UtcNow)
                .SetProperty(instance => instance.ConcurrencyToken, Guid.NewGuid().ToString("N")),
                cancellationToken)
            .ConfigureAwait(false);

        return claimed > 0;
    }

    private static WorkflowInstance ToInstance(WorkflowInstanceEntity entity)
    {
        var instance = new WorkflowInstance
        {
            Id = entity.Id,
            WorkflowName = entity.WorkflowName,
            CurrentStep = entity.CurrentStep,
            Status = entity.Status,
            RetryCount = entity.RetryCount,
            LastError = entity.LastError,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            ConcurrencyToken = entity.ConcurrencyToken,
            ClaimedBy = entity.ClaimedBy,
            ClaimedUntilUtc = entity.ClaimedUntilUtc is { } until
                ? new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc))
                : null,
        };

        instance.CompletedSteps.AddRange(
            JsonSerializer.Deserialize<List<string>>(entity.CompletedStepsJson, SerializerOptions) ?? []);

        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.DataJson, SerializerOptions);
        if (data is not null)
            foreach (var (key, value) in data)
                instance.Data[key] = value;

        return instance;
    }
}
