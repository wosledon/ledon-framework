using System.Diagnostics.Metrics;

namespace Ledon.Queues;

/// <summary>
/// 后台队列 / 发件箱指标（OpenTelemetry Meter <c>Ledon.Queues</c>）。
/// 由 <c>AddLedonObservability()</c> 的 MeterProvider 自动收集，也可自行用 MeterListener 消费。
/// </summary>
public static class QueueMetrics
{
    /// <summary>Meter 名（与 OTel 资源关联，Prometheus 指标名前缀为 ledon_）。</summary>
    public const string MeterName = "Ledon.Queues";

    /// <summary>队列指标 Meter。</summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>发件箱投递成功的消息数。</summary>
    public static readonly Counter<long> OutboxDispatched =
        Meter.CreateCounter<long>("ledon.outbox.dispatched", "件", "发件箱投递成功的消息数");

    /// <summary>发件箱投递失败、进入重试的次数。</summary>
    public static readonly Counter<long> OutboxRetried =
        Meter.CreateCounter<long>("ledon.outbox.retried", "件", "发件箱投递失败进入重试的次数");

    /// <summary>搁置（重试超上限）的消息数（按次累计）。</summary>
    public static readonly Counter<long> OutboxDeadLetters =
        Meter.CreateCounter<long>("ledon.outbox.dead_letters", "件", "搁置（重试超上限）的消息数");

    /// <summary>清理的已处理消息数。</summary>
    public static readonly Counter<long> OutboxCleaned =
        Meter.CreateCounter<long>("ledon.outbox.cleaned", "件", "清理的超保留期已处理消息数");

    /// <summary>收件箱清理的已完成消息数。</summary>
    public static readonly Counter<long> InboxCleaned =
        Meter.CreateCounter<long>("ledon.inbox.cleaned", "件", "清理的超保留期已完成收件箱消息数");

    /// <summary>收件箱去重跳过的重复投递数。</summary>
    public static readonly Counter<long> InboxSkipped =
        Meter.CreateCounter<long>("ledon.inbox.skipped", "件", "收件箱去重跳过的重复投递数");

    /// <summary>后台队列处理成功的任务数。</summary>
    public static readonly Counter<long> QueueProcessed =
        Meter.CreateCounter<long>("ledon.queue.processed", "件", "后台队列处理成功的任务数");

    /// <summary>后台队列处理失败的任务数。</summary>
    public static readonly Counter<long> QueueFailed =
        Meter.CreateCounter<long>("ledon.queue.failed", "件", "后台队列处理失败的任务数");
}
