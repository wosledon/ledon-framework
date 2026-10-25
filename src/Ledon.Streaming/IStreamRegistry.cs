namespace Ledon.Streaming;

/// <summary>在线流注册表抽象。</summary>
public interface IStreamRegistry
{
    /// <summary>开始推流。</summary>
    void OnPublish(
        StreamIdentity identity,
        string? clientId,
        string? clientIp,
        StreamProtocol? protocol,
        IReadOnlyDictionary<string, string>? parameters);

    /// <summary>开始播放。</summary>
    void OnPlay(StreamIdentity identity, string? clientId);

    /// <summary>停止（<paramref name="isPublisher"/> = true 表示推流方断开）。</summary>
    void OnStop(StreamIdentity identity, string? clientId, bool isPublisher);

    /// <summary>全部流的快照。</summary>
    IReadOnlyList<StreamStatus> Snapshot();

    /// <summary>查单条流。</summary>
    StreamStatus? Find(StreamIdentity identity);

    /// <summary>当前观众数。</summary>
    int PlayerCount(StreamIdentity identity);
}
