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

            // 恢复查询按状态过滤 + 更新时间排序（UpdatedAtUtc：SQLite 不支持 DateTimeOffset 排序）
            entity.HasIndex(instance => instance.Status);
            entity.HasIndex(instance => instance.UpdatedAtUtc);
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

        var entity = await context.WorkflowInstances.FindAsync([instance.Id], cancellationToken);
        entity ??= new WorkflowInstanceEntity { Id = instance.Id };

        entity.WorkflowName = instance.WorkflowName;
        entity.CurrentStep = instance.CurrentStep;
        entity.Status = instance.Status;
        entity.RetryCount = instance.RetryCount;
        entity.LastError = instance.LastError;
        entity.CreatedAt = instance.CreatedAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.CompletedStepsJson = JsonSerializer.Serialize(instance.CompletedSteps, SerializerOptions);
        entity.DataJson = JsonSerializer.Serialize(
            instance.Data.ToDictionary(pair => pair.Key, pair => pair.Value), SerializerOptions);

        if (context.Entry(entity).State == EntityState.Detached)
            context.WorkflowInstances.Add(entity);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowInstance?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.WorkflowInstances.FindAsync([id], cancellationToken);
        return entity is null ? null : ToInstance(entity);
    }

    public async Task<IReadOnlyList<WorkflowInstance>> GetResumableAsync(
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await context.WorkflowInstances
            .Where(instance => instance.Status == WorkflowStatus.Running)
            .OrderBy(instance => instance.UpdatedAtUtc)
            .Take(batchSize)
            .AsAsyncEnumerable()
            .Select(entity => ToInstance(entity))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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
