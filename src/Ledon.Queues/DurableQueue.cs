using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ledon.Queues;

/// <summary>
/// 持久化后台队列表：与业务数据同事务写入（宿主进程重启后任务仍在）。
/// 由持久化队列调度器扫描投递（AddLedonDurableQueue）。
/// </summary>
public class QueuedJobMessage
{
    public Guid Id { get; set; }

    /// <summary>队列名（同一应用可有多条队列）。</summary>
    public string QueueName { get; set; } = string.Empty;

    /// <summary>任务负载（JSON）。</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ProcessedAt { get; set; }

    public int RetryCount { get; set; }

    public string? Error { get; set; }

    /// <summary>可用时刻（延迟任务用；立即任务等于 CreatedAt）。</summary>
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 认领者标识（哪个实例正在处理）。多实例部署时靠它保证同一个任务
    /// 同一时刻只有一个实例在执行——否则每个实例都会执行全部待处理任务（N 倍重复）。
    /// </summary>
    public string? LeasedBy { get; set; }

    /// <summary>认领租约到期时刻；null 或已过期 = 可被重新认领（节点崩溃后的接管路径）。</summary>
    public DateTime? LeasedUntil { get; set; }
}

/// <summary>队列默认参数（持久化部分）。</summary>
public sealed class LedonDurableQueueOptions
{
    /// <summary>轮询间隔，默认 1 秒。</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>每轮批大小，默认 20。</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>单条最大重试次数（超过搁置），默认 5。</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>已完成任务保留时长（默认 1 天；TimeSpan.Zero = 不清理）。</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// 单条任务的认领租约，默认 1 分钟：节点崩溃后最多这么久就有别的实例接手。
    /// 调大可以减少"执行慢于租约"时的重复执行，但崩溃恢复会变慢。
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// 持久化任务描述（入队时写入数据库；处理器按 <see cref="HandlerName"/> 解析）。
/// 与进程内 <see cref="IBackgroundQueue"/> 同语义，但任务不随进程消失。
/// </summary>
public sealed record DurableJob(string HandlerName, string Payload, TimeSpan? Delay = null);

/// <summary>持久化任务处理器（按名字解析；实现类自动发现）。</summary>
public interface IDurableJobHandler
{
    /// <summary>处理器名（入队时指定，稳定标识）。</summary>
    string Name { get; }

    /// <summary>执行任务。</summary>
    Task ExecuteAsync(string payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化队列契约：任务落库（宿主重启后仍在），由 <c>AddLedonDurableQueue</c>
/// 注册的调度器按轮询投递给 <see cref="IDurableJobHandler"/>（至少一次语义）。
/// </summary>
public interface IDurableQueue
{
    /// <summary>入队（与业务数据同事务时由调用方负责 SaveChanges）。</summary>
    Task EnqueueAsync(DurableJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取一批待处理任务**并原子认领**：只有第一个认领的实例能拿到
    /// （多实例部署时同一个任务不会被每个实例各执行一遍）。
    /// </summary>
    /// <param name="batchSize">单轮最多认领多少个任务。</param>
    /// <param name="owner">认领者标识（实例唯一）。</param>
    /// <param name="lease">租约时长；到期未处理完别的实例可接手（节点崩溃兜底）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<QueuedJobMessage>> ClaimAsync(
        int batchSize, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>标记完成（同时释放认领）。</summary>
    Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>标记失败（重试计数 +1，同时释放认领以便下轮重试）。</summary>
    Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default);

    /// <summary>清理超保留期的已完成任务。</summary>
    Task<int> DeleteProcessedAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}

/// <summary>EF 实现的持久化队列。</summary>
internal sealed class EfDurableQueue(OutboxDbContext context) : IDurableQueue
{
    public async Task EnqueueAsync(DurableJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        context.QueuedJobs.Add(new QueuedJobMessage
        {
            Id = Guid.NewGuid(),
            QueueName = job.HandlerName,
            Payload = job.Payload,
            CreatedAt = DateTime.UtcNow,
            AvailableAt = DateTime.UtcNow + (job.Delay ?? TimeSpan.Zero),
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QueuedJobMessage>> ClaimAsync(
        int batchSize,
        string owner,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(lease);

        var candidates = await context.QueuedJobs
            .Where(job => job.ProcessedAt == null
                && job.AvailableAt <= now
                && (job.LeasedUntil == null
                    || job.LeasedUntil < now
                    || job.LeasedBy == owner))
            .OrderBy(job => job.CreatedAt)
            .Take(batchSize)
            .Select(job => job.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Length == 0)
            return [];

        // 逐条条件认领：并发部署下只有一个实例能改成功
        var owned = new List<Guid>(candidates.Length);

        foreach (var id in candidates)
        {
            var rows = await context.QueuedJobs
                .Where(job => job.Id == id
                    && (job.LeasedUntil == null
                        || job.LeasedUntil < now
                        || job.LeasedBy == owner))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.LeasedBy, owner)
                    .SetProperty(job => job.LeasedUntil, until),
                    cancellationToken)
                .ConfigureAwait(false);

            if (rows == 1)
                owned.Add(id);
        }

        if (owned.Count == 0)
            return [];

        return await context.QueuedJobs
            .Where(job => owned.Contains(job.Id))
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await context.QueuedJobs
            .Where(job => job.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.ProcessedAt, DateTime.UtcNow)
                .SetProperty(job => job.Error, (string?)null)
                .SetProperty(job => job.LeasedBy, (string?)null)
                .SetProperty(job => job.LeasedUntil, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default)
    {
        await context.QueuedJobs
            .Where(job => job.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.RetryCount, job => job.RetryCount + 1)
                .SetProperty(job => job.Error, error)
                .SetProperty(job => job.LeasedBy, (string?)null)
                .SetProperty(job => job.LeasedUntil, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> DeleteProcessedAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
            return 0;

        var cutoff = DateTime.UtcNow - retention;
        return await context.QueuedJobs
            .Where(job => job.ProcessedAt != null && job.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
