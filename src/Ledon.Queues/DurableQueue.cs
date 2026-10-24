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

    /// <summary>取一批待处理任务。</summary>
    Task<IReadOnlyList<QueuedJobMessage>> DequeueAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>标记完成。</summary>
    Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>标记失败（重试计数 +1）。</summary>
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

    public async Task<IReadOnlyList<QueuedJobMessage>> DequeueAsync(
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await context.QueuedJobs
            .Where(job => job.ProcessedAt == null && job.AvailableAt <= DateTime.UtcNow)
            .OrderBy(job => job.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await context.QueuedJobs.FindAsync([id], cancellationToken);
        if (job is not null)
        {
            job.ProcessedAt = DateTime.UtcNow;
            job.Error = null;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default)
    {
        var job = await context.QueuedJobs.FindAsync([id], cancellationToken);
        if (job is not null)
        {
            job.RetryCount++;
            job.Error = error;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
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
