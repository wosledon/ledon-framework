namespace Ledon.Streaming.Rtmp;

/// <summary>RTMP 消息类型 ID（Chunk Message Header 的 Message Type ID）。</summary>
public enum RtmpMessageType : int
{
    /// <summary>协议控制消息 1：设置块大小。</summary>
    SetChunkSize = 1,

    /// <summary>协议控制消息 2：放弃（丢弃未读消息）。</summary>
    Abort = 2,

    /// <summary>协议控制消息 3：确认（接收窗口）。</summary>
    Acknowledgement = 3,

    /// <summary>协议控制消息 4：用户控制（PingRequest/StreamBegin 等）。</summary>
    UserControl = 4,

    /// <summary>协议控制消息 5：窗口确认大小。</summary>
    WindowAcknowledgementSize = 5,

    /// <summary>协议控制消息 6：设置对端带宽。</summary>
    SetPeerBandwidth = 6,

    /// <summary>音频数据。</summary>
    Audio = 8,

    /// <summary>视频数据。</summary>
    Video = 9,

    /// <summary>AMF0 元数据/命令（Data）。</summary>
    DataAmf0 = 18,

    /// <summary>AMF0 命令（NetConnection）。</summary>
    CommandAmf0 = 20,

    /// <summary>聚合消息。</summary>
    Aggregate = 22,

    /// <summary>AMF3 Data。</summary>
    DataAmf3 = 15,

    /// <summary>AMF3 共享对象。</summary>
    SharedObjectAmf3 = 16,

    /// <summary>AMF3 命令。</summary>
    CommandAmf3 = 17,
}

/// <summary>
/// 一个完整的 RTMP 消息（分块流重组后的产物）。
/// </summary>
public sealed class RtmpMessage
{
    /// <summary>消息类型。</summary>
    public required RtmpMessageType MessageType { get; init; }

    /// <summary>消息流 ID（NetStream）。</summary>
    public int StreamId { get; init; }

    /// <summary>时间戳（毫秒，相对流起点；可为增量或绝对值，由格式决定）。</summary>
    public uint TimestampMs { get; init; }

    /// <summary>负载。</summary>
    public required byte[] Payload { get; init; }
}

/// <summary>RTMP 分块格式（Chunk Message Header 的 fmt）。</summary>
public enum RtmpChunkFormat : byte
{
    /// <summary>11 字节：完整头（时间戳+长度+类型+流ID）。</summary>
    Type0 = 0,

    /// <summary>7 字节：相对时间戳+长度+类型。</summary>
    Type1 = 1,

    /// <summary>3 字节：时间戳增量。</summary>
    Type2 = 2,

    /// <summary>0 字节：仅流 ID（复用上一条的所有字段）。</summary>
    Type3 = 3,
}
