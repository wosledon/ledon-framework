using Ledon.Queues;
using Microsoft.Extensions.DependencyInjection;
using Ledon.Scheduling;
using Ledon.Workflow;

namespace Ledon.Admin;

/// <summary>运维快照：把分散在各能力包里的运行状态聚成一张图（只读）。</summary>
public sealed class OpsSnapshot
{
    /// <summary>快照时间（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>发件箱：待投递 / 死信 / 已投递（未接入发件箱时为 <c>null</c>）。</summary>
    public OutboxStats? Outbox { get; init; }

    /// <summary>后台队列与投递指标（累计值）。</summary>
    public QueueStats Queues { get; init; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>工作流：可恢复（运行中）实例数（未接入工作流时为 <c>null</c>）。</summary>
    public WorkflowStats? Workflows { get; init; }

    /// <summary>定时任务：任务数与近期失败数（未接入调度时为 <c>null</c>）。</summary>
    public SchedulerStats? Scheduler { get; init; }
}

/// <summary>发件箱统计。</summary>
public sealed record OutboxStats(int Pending, int DeadLetters);

/// <summary>队列/投递指标（累计）。</summary>
public sealed record QueueStats(
    long OutboxDispatched,
    long OutboxRetried,
    long OutboxDeadLetters,
    long QueueProcessed,
    long QueueFailed,
    long InboxSkipped);

/// <summary>工作流统计。</summary>
public sealed record WorkflowStats(int Running);

/// <summary>定时任务统计。</summary>
public sealed record SchedulerStats(int Tasks, int RecentFailures);

/// <summary>
/// 采集运维快照。各能力包都是可选的：没接入的项在快照里就是 <c>null</c>——
/// 一个接口回答"系统现在健康吗"，不用分别登每套系统。
/// </summary>
public sealed class OpsSnapshotService(
    IServiceProvider services,
    QueueMetricsReader metrics)
{
    public async Task<OpsSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var outboxStore = services.GetService<IOutboxStore>();
        var workflowStore = services.GetService<IWorkflowStore>();
        var invoker = services.GetService<ISchedulerInvoker>();
        var history = services.GetService<IScheduledTaskExecutionHistory>();

        OutboxStats? outbox = null;
        if (outboxStore is not null)
        {
            // 只数个数，不认领：运维快照不该把消息的租约抢过来
            var dead = await outboxStore.GetDeadLettersAsync(1000, cancellationToken).ConfigureAwait(false);
            outbox = new OutboxStats(await outboxStore.CountPendingAsync(cancellationToken), dead.Count);
        }

        WorkflowStats? workflows = null;
        if (workflowStore is not null)
        {
            var resumable = await workflowStore.GetResumableAsync(1000, cancellationToken).ConfigureAwait(false);
            workflows = new WorkflowStats(resumable.Count);
        }

        SchedulerStats? scheduler = null;
        if (invoker is not null)
        {
            var recent = history?.Snapshot() ?? [];
            scheduler = new SchedulerStats(invoker.TaskNames.Count, recent.Count(entry => !entry.Success));
        }

        var snapshot = metrics.Snapshot();

        return new OpsSnapshot
        {
            Outbox = outbox,
            Queues = new QueueStats(
                metrics.Value(QueueMetrics.OutboxDispatched.Name),
                metrics.Value(QueueMetrics.OutboxRetried.Name),
                metrics.Value(QueueMetrics.OutboxDeadLetters.Name),
                metrics.Value(QueueMetrics.QueueProcessed.Name),
                metrics.Value(QueueMetrics.QueueFailed.Name),
                metrics.Value(QueueMetrics.InboxSkipped.Name)),
            Workflows = workflows,
            Scheduler = scheduler,
        };
    }
}
