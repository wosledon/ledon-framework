using Ledon.Streaming.Rtmp;

namespace Ledon.Streaming;

/// <summary>在线流信息（由进程内媒体面直接提供，不查询外部服务器）。</summary>
public sealed record MediaStreamInfo(
    StreamIdentity Identity,
    bool IsPublishing,
    int PlayerCount,
    string? VideoCodec = null,
    int? Width = null,
    int? Height = null,
    int? VideoBitrateKbps = null,
    int? AudioBitrateKbps = null);

/// <summary>
/// 流控制面：查询在线流、断开推流/播放连接。
/// 全部由进程内的 <see cref="RtmpServer"/> 与 <see cref="IStreamRegistry"/> 实现，
/// 不依赖 SRS / ZLMediaKit 等外部媒体服务器。
/// </summary>
public interface IStreamControl
{
    /// <summary>当前在线流快照。</summary>
    IReadOnlyList<MediaStreamInfo> GetStreams();

    /// <summary>
    /// 断开指定流的连接。传 <paramref name="clientId"/> 时只断该客户端；
    /// 返回实际断开的连接数。
    /// </summary>
    int Kickoff(StreamIdentity identity, string? clientId = null);
}

/// <summary>进程内流控制面实现。</summary>
public sealed class StreamControlService(IStreamRegistry registry, RtmpServer server) : IStreamControl
{
    public IReadOnlyList<MediaStreamInfo> GetStreams() =>
        registry.Snapshot()
            .Where(status => status.Online)
            .Select(status => new MediaStreamInfo(
                status.Identity,
                status.Online,
                status.PlayerCount))
            .ToArray();

    public int Kickoff(StreamIdentity identity, string? clientId = null) =>
        server.Kickoff(identity, clientId);
}
