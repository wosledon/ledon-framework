using System.Collections.Concurrent;
using Ledon.Streaming.Media;
using Microsoft.Extensions.Logging;

namespace Ledon.Streaming.Playback;

/// <summary>流枢纽选项。</summary>
public sealed class StreamHubOptions
{
    /// <summary>
    /// 停止发布后流的保留时长，默认 10 秒。观众断线重连、HLS 追赶都要靠这段缓冲；
    /// 到期还没人重新发布就删掉——否则每条播过的流都永久留在字典里（内存泄漏）。
    /// </summary>
    public TimeSpan UnpublishedRetention { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>是否启用过期清扫，默认 <c>true</c>。</summary>
    public bool SweepExpired { get; set; } = true;

    /// <summary>最大并发流数，默认 1000（超出时拒绝新发布，防洪水）。</summary>
    public int MaxStreams { get; set; } = 1000;
}

/// <summary>
/// 流枢纽：管理全部直播流的生命周期与媒体分发。
/// 单例；多实例部署时每个实例管理自己接进来的流（媒体面天然是每实例独立的）。
/// <para>
/// 生命周期：发布 → 分发 → 停止发布 → 保留 <see cref="StreamHubOptions.UnpublishedRetention"/>
/// → 清扫。没有这一步，每条播过的流都会永远占着内存。
/// </para>
/// </summary>
public sealed class StreamHub
{
    private readonly ConcurrentDictionary<string, MediaStream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<StreamHub> _logger;
    private readonly StreamHubOptions _options;

    public StreamHub(ILogger<StreamHub> logger, StreamHubOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new StreamHubOptions();
    }

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
        if (!_streams.ContainsKey(identity.Path) && _streams.Count >= _options.MaxStreams)
            throw new InvalidOperationException(
                $"并发流数已达上限 {_options.MaxStreams}，拒绝发布 {identity.Path}");

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

    /// <summary>
    /// 清扫已停止发布且超过保留时长的流（宿主定时调用，或 <c>StreamHubSweeper</c> 自动调）。
    /// </summary>
    /// <returns>删除的流数。</returns>
    public int SweepExpired()
    {
        if (!_options.SweepExpired)
            return 0;

        var deadline = DateTimeOffset.UtcNow - _options.UnpublishedRetention;
        var removed = 0;

        foreach (var stream in _streams.Values)
        {
            if (stream.IsPublished || stream.UnpublishedAtUtc is not { } stoppedAt || stoppedAt > deadline)
                continue;

            if (_streams.TryRemove(stream.Identity.Path, out _))
            {
                removed++;
                _logger.LogInformation("流 {Stream} 已过期回收（{Seconds} 秒内无人重新发布）",
                    stream.Identity.Path, _options.UnpublishedRetention.TotalSeconds);
            }
        }

        return removed;
    }

    /// <summary>统计快照（运维端点用）。</summary>
    public IReadOnlyList<MediaStreamStats> Stats() =>
        _streams.Values.Select(stream => stream.Stats()).ToArray();
}
