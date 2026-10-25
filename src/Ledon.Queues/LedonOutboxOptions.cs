namespace Ledon.Queues;

/// <summary>发件箱调度默认参数。</summary>
public sealed class LedonOutboxOptions
{
    /// <summary>轮询间隔，默认 5 秒。</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>每轮批大小，默认 20。</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>单条消息最大重试次数（超过即搁置），默认 5。</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// 已处理消息保留时长，默认 7 天：调度器按 <see cref="CleanupInterval"/> 清理更早的已处理消息
    /// （表无维护会无限增长）。<see cref="TimeSpan.Zero"/> = 不自动清理（自行管理）。
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>清理与死信检查的间隔，默认 1 小时。</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 单条消息的认领租约，默认 1 分钟：节点崩溃后最多这么久就有别的实例接手。
    /// 调大可以减少"处理慢于租约"时的重复投递，但崩溃恢复会变慢。
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);
}
