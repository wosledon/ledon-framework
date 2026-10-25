using System.Buffers;
using System.Buffers.Binary;

namespace Ledon.Streaming.Media;

/// <summary>FLV Tag 类型。</summary>
public enum FlvTagType : byte
{
    /// <summary>音频。</summary>
    Audio = 8,

    /// <summary>视频。</summary>
    Video = 9,

    /// <summary>脚本数据（onMetaData 等）。</summary>
    Script = 18,
}

/// <summary>FLV 帧类型（视频 Tag 高 4 位）。</summary>
public enum FlvFrameType : byte
{
    /// <summary>关键帧（可独立解码）。</summary>
    KeyFrame = 1,

    /// <summary>帧间帧。</summary>
    InterFrame = 2,

    /// <summary> disposable 帧。</summary>
    DisposableInterFrame = 3,

    /// <summary>生成关键帧。</summary>
    GeneratedKeyFrame = 4,

    /// <summary>视频信息帧。</summary>
    VideoInfoFrame = 5,
}

/// <summary>FLV Codec ID（视频 Tag 低 4 位）。</summary>
public enum FlvVideoCodec : byte
{
    /// <summary>H.264（AVC）。</summary>
    Avc = 7,

    /// <summary>H.265（HEVC）。</summary>
    Hevc = 12,
}

/// <summary>FLV 音频编码。</summary>
public enum FlvAudioCodec : byte
{
    /// <summary>PCM。</summary>
    Pcm = 0,

    /// <summary>ADPCM。</summary>
    Adpcm = 1,

    /// <summary>MP3。</summary>
    Mp3 = 2,

    /// <summary>AAC。</summary>
    Aac = 10,
}

/// <summary>
/// 一个 FLV Tag（音视频帧或脚本数据）。这是媒体流的最小单位：
/// RTMP 消息、FLV 文件、HLS 都以它为中心转换。
/// </summary>
public sealed class FlvTag
{
    /// <summary>Tag 类型。</summary>
    public required FlvTagType TagType { get; init; }

    /// <summary>时间戳（毫秒，相对流起点）。</summary>
    public uint TimestampMs { get; init; }

    /// <summary>数据（视频含 1 字节帧类型+CodecID 前缀；音频含 1 字节前缀）。</summary>
    public required byte[] Data { get; init; }

    /// <summary>是否为关键帧（视频）。</summary>
    public bool IsKeyFrame =>
        TagType == FlvTagType.Video
        && Data.Length > 0
        && ((FlvFrameType)(Data[0] >> 4)) is FlvFrameType.KeyFrame or FlvFrameType.GeneratedKeyFrame;
}

/// <summary>
/// FLV 序列化：写文件头（"FLV" + 版本 + 标志 + 头长）与 Tag（含 PreviousTagSize）。
/// </summary>
public static class Flv
{
    /// <summary>FLV 文件头（9 字节）+ 首个 PreviousTagSize（4 字节）。</summary>
    public const int HeaderSize = 13;

    /// <summary>写 FLV 头（有视频/音频分别置位）。</summary>
    public static void WriteHeader(IBufferWriter<byte> writer, bool hasVideo = true, bool hasAudio = true)
    {
        var span = writer.GetSpan(HeaderSize);
        span[0] = (byte)'F';
        span[1] = (byte)'L';
        span[2] = (byte)'V';
        span[3] = 1;                                                      // 版本
        span[4] = (byte)((hasAudio ? 4 : 0) | (hasVideo ? 1 : 0));       // 标志位
        BinaryPrimitives.WriteUInt32BigEndian(span[5..], 9);             // 头长
        BinaryPrimitives.WriteUInt32BigEndian(span[9..], 0);             // PreviousTagSize0
        writer.Advance(HeaderSize);
    }

    /// <summary>写一个 Tag（11 字节 Tag 头 + 数据 + 4 字节 PreviousTagSize）。</summary>
    public static void WriteTag(IBufferWriter<byte> writer, FlvTag tag)
    {
        var header = writer.GetSpan(11);
        header[0] = (byte)tag.TagType;

        // 24 位数据长度（大端）
        var length = tag.Data.Length;
        header[1] = (byte)(length >> 16);
        header[2] = (byte)(length >> 8);
        header[3] = (byte)length;

        // 时间戳：低 24 位在 4-6 字节，高 8 位在第 8 字节（ExtendedTimestamp）
        header[4] = (byte)(tag.TimestampMs >> 16);
        header[5] = (byte)(tag.TimestampMs >> 8);
        header[6] = (byte)tag.TimestampMs;
        header[7] = (byte)(tag.TimestampMs >> 24);
        header[8] = 0;      // StreamID（始终 0）
        header[9] = 0;
        header[10] = 0;
        writer.Advance(11);

        writer.Write(tag.Data);

        var trailer = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(trailer, (uint)(length + 11));
        writer.Advance(4);
    }
}
