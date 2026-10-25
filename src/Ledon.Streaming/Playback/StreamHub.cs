using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Ledon.Streaming.Playback;

/// <summary>
/// 流枢纽：管理全部直播流的生命周期与媒体分发。
/// 单例；多实例部署时每个实例管理自己接进来的流（媒体面天然是每实例独立的）。
/// </summary>
public sealed class StreamHub
{
    private readonly ConcurrentDictionary<string, MediaStream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<StreamHub> _logger;

    public StreamHub(ILogger<StreamHub> logger) => _logger = logger;

    /// <summary>全部流（运维/统计）。</summary>
    public IReadOnlyList<MediaStream> Streams => _streams.Values.ToArray();

    /// <summary>取流（不存在返回 null）。</summary>
    public MediaStream? Find(StreamIdentity identity) =>
        _streams.TryGetValue(identity.Path, out var stream) ? stream : null;

    /// <summary>取流，不存在则创建。</summary>
    public MediaStream GetOrAdd(StreamIdentity identity) =>
        _streams.GetOrAdd(identity.Path, _ => new MediaStream(identity, _logger));

    /// <summary>开始发布。</summary>
    public MediaStream Publish(StreamIdentity identity, string? publisherId = null)
    {
        var stream = GetOrAdd(identity);
        stream.Publish(publisherId);

        return stream;
    }

    /// <summary>停止发布（保留 GOP 缓存一小段时间，方便断线重连的观众）。</summary>
    public void Unpublish(StreamIdentity identity) => Find(identity)?.Unpublish();

    /// <summary>订阅（返回 null 表示流不存在或未在发布）。</summary>
    public StreamSubscription? Subscribe(StreamIdentity identity)
    {
        var stream = Find(identity);
        if (stream is null || !stream.IsPublished)
            return null;

        return stream.Subscribe();
    }

    /// <summary>取消订阅。</summary>
    public void Unsubscribe(StreamIdentity identity, Guid subscriptionId) =>
        Find(identity)?.Unsubscribe(subscriptionId);

    /// <summary>写入媒体 Tag（发布者路径）。</summary>
    public void Write(StreamIdentity identity, Media.FlvTag tag) =>
        GetOrAdd(identity).Write(tag);

    /// <summary>统计快照（运维端点用）。</summary>
    public IReadOnlyList<MediaStreamStats> Stats() =>
        _streams.Values.Select(stream => stream.Stats()).ToArray();
}
