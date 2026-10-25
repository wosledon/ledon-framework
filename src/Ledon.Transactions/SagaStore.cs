using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ledon.Transactions;

/// <summary>Saga 实例的持久化实体（EF Core 映射）。</summary>
public class SagaInstanceEntity
{
    /// <summary>实例 ID（业务提供的幂等键，或生成的 GUID）。</summary>
    public Guid Id { get; set; }

    /// <summary>幂等键：同一个键只会有一个 Saga（重复提交返回同一实例）。</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Saga 名称（日志/诊断用）。</summary>
    public string SagaName { get; set; } = string.Empty;

    /// <summary>当前步骤名（null = 尚未开始）。</summary>
    public string? CurrentStep { get; set; }

    /// <summary>实例状态。</summary>
    public SagaStatus Status { get; set; }

    /// <summary>步骤清单（JSON 数组，保序——逆序补偿要靠它）。</summary>
    public string StepsJson { get; set; } = "[]";

    /// <summary>已成功完成的步骤名（JSON 数组，保序）。</summary>
    public string CompletedStepsJson { get; set; } = "[]";

    /// <summary>已补偿的步骤名（JSON 数组，保序）。</summary>
    public string CompensatedStepsJson { get; set; } = "[]";

    /// <summary>共享数据（JSON 对象）。</summary>
    public string DataJson { get; set; } = "{}";

    /// <summary>最近一次错误信息。</summary>
    public string? LastError { get; set; }

    /// <summary>重试次数（当前步骤）。</summary>
    public int RetryCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>更新时间（UTC 的 DateTime）——SQLite 等提供程序不支持 DateTimeOffset 排序，用这列。</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>乐观并发令牌（映射为并发令牌，防多节点同时保存丢更新）。</summary>
    public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>当前持有租约的节点。</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>租约到期时间（UTC 的 DateTime）。</summary>
    public DateTime? ClaimedUntilUtc { get; set; }
}

/// <summary>Saga 实例状态。</summary>
public enum SagaStatus
{
    /// <summary>已创建，未开始。</summary>
    Created,

    /// <summary>执行中。</summary>
    Running,

    /// <summary>全部步骤成功。</summary>
    Completed,

    /// <summary>某步失败且补偿完成。</summary>
    Compensated,

    /// <summary>补偿也失败（需人工介入）。</summary>
    Failed,
}

/// <summary>
/// 带 Saga 存储的 DbContext 基类：用户的 DbContext 继承它即获得实例表
/// （与 <c>WorkflowDbContext</c> / <c>OutboxDbContext</c> 同一风格）。
/// </summary>
public abstract class SagaDbContext : DbContext
{
    protected SagaDbContext()
    {
    }

    protected SagaDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <summary>Saga 实例表。</summary>
    public DbSet<SagaInstanceEntity> SagaInstances => Set<SagaInstanceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SagaInstanceEntity>(entity =>
        {
            entity.HasKey(instance => instance.Id);
            entity.Property(instance => instance.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(instance => instance.SagaName).IsRequired().HasMaxLength(200);
            entity.Property(instance => instance.CurrentStep).HasMaxLength(200);
            entity.Property(instance => instance.StepsJson).IsRequired();
            entity.Property(instance => instance.CompletedStepsJson).IsRequired();
            entity.Property(instance => instance.CompensatedStepsJson).IsRequired();
            entity.Property(instance => instance.DataJson).IsRequired();

            // 乐观并发令牌：EF 会把它并进 UPDATE 的 WHERE，影响 0 行即抛并发异常
            entity.Property(instance => instance.ConcurrencyToken).IsConcurrencyToken();

            // 幂等键唯一：同一业务键只会有一个 Saga 实例
            entity.HasIndex(instance => instance.IdempotencyKey).IsUnique();
            entity.HasIndex(instance => instance.Status);
            entity.HasIndex(instance => instance.UpdatedAtUtc);
            entity.HasIndex(instance => instance.ClaimedUntilUtc);
        });

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// Saga 实例（运行时视图）。步骤定义随实例持久化——恢复后不需要调用方重新
/// 提供步骤清单（进程重启后 <c>saga.For(...)</c> 的构建器早就不在了）。
/// </summary>
public sealed class SagaInstance
{
    /// <summary>实例 ID。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>幂等键。</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Saga 名称。</summary>
    public string SagaName { get; set; } = string.Empty;

    /// <summary>当前步骤名（null = 尚未开始）。</summary>
    public string? CurrentStep { get; set; }

    /// <summary>状态。</summary>
    public SagaStatus Status { get; set; } = SagaStatus.Created;

    /// <summary>步骤名（保序）。</summary>
    public List<string> Steps { get; } = [];

    /// <summary>已成功完成的步骤名（保序）。</summary>
    public List<string> CompletedSteps { get; } = [];

    /// <summary>已补偿的步骤名（保序）。</summary>
    public List<string> CompensatedSteps { get; } = [];

    /// <summary>最近一次错误。</summary>
    public string? LastError { get; set; }

    /// <summary>当前步骤重试次数。</summary>
    public int RetryCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>乐观并发令牌。</summary>
    public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>当前持有租约的节点。</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>租约到期时间（UTC）。</summary>
    public DateTimeOffset? ClaimedUntilUtc { get; set; }

    /// <summary>共享数据（JSON 序列化后的键值；复杂对象由业务自行转字符串）。</summary>
    public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Saga 存储抽象：实例 + 每步完成/补偿状态的持久化，供崩溃恢复与多节点认领。
/// </summary>
public interface ISagaStore
{
    /// <summary>保存实例（不存在则创建）。</summary>
    Task SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default);

    /// <summary>按 ID 读取；不存在返回 null。</summary>
    Task<SagaInstance?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>按幂等键读取；不存在返回 null。</summary>
    Task<SagaInstance?> GetByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>列出可恢复的实例（Running 且租约已过期/未认领）。</summary>
    Task<IReadOnlyList<SagaInstance>> GetResumableAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>原子认领一个实例；只有第一个认领的节点拿到 <c>true</c>。</summary>
    Task<bool> ClaimAsync(Guid id, string owner, TimeSpan lease, CancellationToken cancellationToken = default);
}

/// <summary>EF 实现的 Saga 存储。</summary>
internal sealed class EfSagaStore(SagaDbContext context) : ISagaStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public async Task SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var steps = JsonSerializer.Serialize(instance.Steps, SerializerOptions);
        var completed = JsonSerializer.Serialize(instance.CompletedSteps, SerializerOptions);
        var compensated = JsonSerializer.Serialize(instance.CompensatedSteps, SerializerOptions);
        var data = JsonSerializer.Serialize(instance.Data, SerializerOptions);
        var now = DateTime.UtcNow;
        var nextToken = Guid.NewGuid().ToString("N");

        // 条件更新：WHERE Id = @id AND ConcurrencyToken = @调用方持有的旧值。
        // 别人改过同一行时旧值就对不上 → 影响 0 行 → 抛并发异常（而不是静默覆盖）
        var updated = await context.SagaInstances
            .Where(entity => entity.Id == instance.Id && entity.ConcurrencyToken == instance.ConcurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entity => entity.IdempotencyKey, instance.IdempotencyKey)
                .SetProperty(entity => entity.SagaName, instance.SagaName)
                .SetProperty(entity => entity.CurrentStep, instance.CurrentStep)
                .SetProperty(entity => entity.Status, instance.Status)
                .SetProperty(entity => entity.StepsJson, steps)
                .SetProperty(entity => entity.CompletedStepsJson, completed)
                .SetProperty(entity => entity.CompensatedStepsJson, compensated)
                .SetProperty(entity => entity.DataJson, data)
                .SetProperty(entity => entity.LastError, instance.LastError)
                .SetProperty(entity => entity.RetryCount, instance.RetryCount)
                .SetProperty(entity => entity.CreatedAt, instance.CreatedAt)
                .SetProperty(entity => entity.UpdatedAt, DateTimeOffset.UtcNow)
                .SetProperty(entity => entity.UpdatedAtUtc, now)
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

        var exists = await context.SagaInstances
            .AnyAsync(entity => entity.Id == instance.Id, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
            throw new DbUpdateConcurrencyException(
                $"Saga 实例 {instance.Id} 已被其他节点修改（并发冲突），本次保存被拒绝");

        context.SagaInstances.Add(new SagaInstanceEntity
        {
            Id = instance.Id,
            IdempotencyKey = instance.IdempotencyKey,
            SagaName = instance.SagaName,
            CurrentStep = instance.CurrentStep,
            Status = instance.Status,
            StepsJson = steps,
            CompletedStepsJson = completed,
            CompensatedStepsJson = compensated,
            DataJson = data,
            LastError = instance.LastError,
            RetryCount = instance.RetryCount,
            CreatedAt = instance.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedAtUtc = now,
            ConcurrencyToken = nextToken,
            ClaimedBy = instance.ClaimedBy,
            ClaimedUntilUtc = instance.ClaimedUntilUtc?.UtcDateTime,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        instance.ConcurrencyToken = nextToken;
    }

    public async Task<SagaInstance?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.SagaInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToInstance(entity);
    }

    public async Task<SagaInstance?> GetByIdempotencyKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.SagaInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(instance => instance.IdempotencyKey == key, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToInstance(entity);
    }

    public async Task<IReadOnlyList<SagaInstance>> GetResumableAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        return await context.SagaInstances
            .AsNoTracking()
            .Where(instance => instance.Status == SagaStatus.Running
                && (instance.ClaimedUntilUtc == null || instance.ClaimedUntilUtc < now))
            .OrderBy(instance => instance.UpdatedAtUtc)
            .Take(batchSize)
            .Select(entity => ToInstance(entity))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ClaimAsync(
        Guid id,
        string owner,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(lease);

        // 原子抢占：只把"未认领或租约已过期"的行改成自己的
        var claimed = await context.SagaInstances
            .Where(instance => instance.Id == id
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

    private static SagaInstance ToInstance(SagaInstanceEntity entity)
    {
        var instance = new SagaInstance
        {
            Id = entity.Id,
            IdempotencyKey = entity.IdempotencyKey,
            SagaName = entity.SagaName,
            CurrentStep = entity.CurrentStep,
            Status = entity.Status,
            LastError = entity.LastError,
            RetryCount = entity.RetryCount,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            ConcurrencyToken = entity.ConcurrencyToken,
            ClaimedBy = entity.ClaimedBy,
            ClaimedUntilUtc = entity.ClaimedUntilUtc is { } until
                ? new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc))
                : null,
        };

        instance.Steps.AddRange(JsonSerializer.Deserialize<List<string>>(entity.StepsJson, SerializerOptions) ?? []);
        instance.CompletedSteps.AddRange(
            JsonSerializer.Deserialize<List<string>>(entity.CompletedStepsJson, SerializerOptions) ?? []);
        instance.CompensatedSteps.AddRange(
            JsonSerializer.Deserialize<List<string>>(entity.CompensatedStepsJson, SerializerOptions) ?? []);

        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.DataJson, SerializerOptions);
        if (data is not null)
            foreach (var (key, value) in data)
                instance.Data[key] = value;

        return instance;
    }
}

/// <summary>进程内 Saga 存储（未配 <see cref="SagaDbContext"/> 时的兜底）。</summary>
internal sealed class InMemorySagaStore : ISagaStore
{
    private readonly ConcurrentDictionary<Guid, SagaInstance> _instances = new();

    public Task SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
    {
        _instances[instance.Id] = instance;
        return Task.CompletedTask;
    }

    public Task<SagaInstance?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_instances.TryGetValue(id, out var instance) ? instance : null);

    public Task<SagaInstance?> GetByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_instances.Values.FirstOrDefault(instance => instance.IdempotencyKey == key));

    public Task<IReadOnlyList<SagaInstance>> GetResumableAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        return Task.FromResult<IReadOnlyList<SagaInstance>>(
            _instances.Values
                .Where(instance => instance.Status == SagaStatus.Running
                    && (instance.ClaimedUntilUtc is null || instance.ClaimedUntilUtc < now))
                .Take(batchSize)
                .ToArray());
    }

    public Task<bool> ClaimAsync(
        Guid id,
        string owner,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(id, out var instance))
            return Task.FromResult(false);

        var now = DateTimeOffset.UtcNow;

        lock (instance)
        {
            if (instance.ClaimedUntilUtc is { } until && until >= now && instance.ClaimedBy != owner)
                return Task.FromResult(false);

            instance.ClaimedBy = owner;
            instance.ClaimedUntilUtc = now.Add(lease);

            return Task.FromResult(true);
        }
    }
}
