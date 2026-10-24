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
}
