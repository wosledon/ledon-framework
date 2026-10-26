using Microsoft.Extensions.Logging;

namespace Ledon.Streaming;

/// <summary>
/// 在线流注册表：由进程内 RTMP 媒体面驱动，内存维护"谁在推、几个人在看"。
/// 单实例部署直接用；多实例部署可换 Redis 实现（同一接口）。
/// </summary>
public sealed class InMemoryStreamRegistry : IStreamRegistry
{
    private readonly Dictionary<string, StreamStatus> _streams = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _gate = new();
    private readonly LedonStreamingOptions _options;
    private readonly ILogger<InMemoryStreamRegistry> _logger;

    public InMemoryStreamRegistry(LedonStreamingOptions options, ILogger<InMemoryStreamRegistry> logger)
    {
        _options = options;
        _logger = logger;
    }

    public void OnPublish(StreamIdentity identity, string? clientId, string? clientIp, StreamProtocol? protocol, IReadOnlyDictionary<string, string>? parameters)
    {
        lock (_gate)
        {
            var status = GetOrAdd(identity);
            status.Online = true;
            status.PublisherId = clientId;
            status.PublisherIp = clientIp;
            status.PublishProtocol = protocol;
            status.StartedAtUtc = DateTimeOffset.UtcNow;
            status.LastSeenUtc = DateTimeOffset.UtcNow;
            status.PlayerCount = 0;
        }

        _logger.LogInformation("流 {Stream} 开始推流（{ClientId} / {Ip}）", identity, clientId, clientIp);
    }

    public void OnPlay(StreamIdentity identity, string? clientId)
    {
        lock (_gate)
        {
            var status = GetOrAdd(identity);
            status.PlayerCount++;
            status.LastSeenUtc = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("流 {Stream} 新增观众（{ClientId}，当前 {Count} 人）", identity, clientId, PlayersOf(identity));
    }

    public void OnHeartbeat(StreamIdentity identity)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(identity.Path, out var status))
                status.LastSeenUtc = DateTimeOffset.UtcNow;
        }
    }

    public void OnStop(StreamIdentity identity, string? clientId, bool isPublisher)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(identity.Path, out var status))
                return;

            status.LastSeenUtc = DateTimeOffset.UtcNow;

            if (isPublisher)
            {
                status.Online = false;
                status.PublisherId = null;
                status.PlayerCount = 0;
            }
            else if (status.PlayerCount > 0)
            {
                status.PlayerCount--;
            }
        }

        _logger.LogInformation("流 {Stream} {What}停止（{ClientId}）", identity, isPublisher ? "推流" : "播放", clientId);
    }

    public IReadOnlyList<StreamStatus> Snapshot()
    {
        lock (_gate)
        {
            return _streams.Values.Select(Clone).ToArray();
        }
    }

    public StreamStatus? Find(StreamIdentity identity)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(identity.Path, out var status) ? Clone(status) : null;
        }
    }

    public int PlayerCount(StreamIdentity identity)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(identity.Path, out var status) ? status.PlayerCount : 0;
        }
    }

    /// <summary>把超过心跳阈值的流标记为离线（定时调用）。</summary>
    public int SweepOffline()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.OfflineThreshold;
        var swept = 0;

        lock (_gate)
        {
            foreach (var status in _streams.Values)
            {
                if (status.Online && status.LastSeenUtc < cutoff)
                {
                    status.Online = false;
                    swept++;
                }
            }
        }

        if (swept > 0)
            _logger.LogWarning("{Count} 条流超过 {Threshold} 无心跳，已标记离线", swept, _options.OfflineThreshold);

        return swept;
    }

    private int PlayersOf(StreamIdentity identity)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(identity.Path, out var status) ? status.PlayerCount : 0;
        }
    }

    private StreamStatus GetOrAdd(StreamIdentity identity)
    {
        if (!_streams.TryGetValue(identity.Path, out var status))
        {
            status = new StreamStatus { Identity = identity };
            _streams[identity.Path] = status;
        }

        return status;
    }

    private static StreamStatus Clone(StreamStatus status) => new()
    {
        Identity = status.Identity,
        Online = status.Online,
        PublisherId = status.PublisherId,
        PublisherIp = status.PublisherIp,
        PublishProtocol = status.PublishProtocol,
        PlayerCount = status.PlayerCount,
        StartedAtUtc = status.StartedAtUtc,
        LastSeenUtc = status.LastSeenUtc,
        Metadata = status.Metadata,
    };
}
