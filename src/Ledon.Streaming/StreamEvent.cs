namespace Ledon.Streaming;

/// <summary>流事件类型（对应媒体服务器回调语义）。</summary>
public enum StreamEventType
{
    /// <summary>开始推流。</summary>
    Publish = 0,

    /// <summary>停止推流。</summary>
    Unpublish = 1,

    /// <summary>开始播放。</summary>
    Play = 2,

    /// <summary>停止播放。</summary>
    Stop = 3,

    /// <summary>录制开始。</summary>
    DvrStarted = 4,

    /// <summary>录制结束。</summary>
    DvrStopped = 5,

    /// <summary>掉线（超过心跳阈值）。</summary>
    Offline = 6,
}

/// <summary>流事件。</summary>
public sealed class StreamEvent
{
    /// <summary>事件类型。</summary>
    public required StreamEventType Type { get; init; }

    /// <summary>流标识。</summary>
    public required StreamIdentity Identity { get; init; }

    /// <summary>客户端 ID（媒体服务器分配）。</summary>
    public string? ClientId { get; init; }

    /// <summary>客户端 IP。</summary>
    public string? ClientIp { get; init; }

    /// <summary>协议。</summary>
    public StreamProtocol? Protocol { get; init; }

    /// <summary>发生时间（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>附加参数（媒体服务器透传的查询参数，含 stream_id、vhost 等）。</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

/// <summary>流事件处理器：上线通知、落库、转推第三方、录制归档都挂在这里。</summary>
public interface IStreamEventHandler
{
    /// <summary>处理事件。抛异常只影响本次事件（已记录日志），不影响其他处理器。</summary>
    Task HandleAsync(StreamEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>处理器基类：只重写关心的事件。</summary>
public abstract class StreamEventHandlerBase : IStreamEventHandler
{
    public virtual Task HandleAsync(StreamEvent @event, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
