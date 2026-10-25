using System.Buffers.Binary;

namespace Ledon.Protocols;

/// <summary>帧编解码：把字节流切成"一个完整包"（协议解析的第一层）。</summary>
public interface IFrameCodec
{
    /// <summary>
    /// 从缓冲区切出尽可能多的完整帧，返回剩余未消费字节数。
    /// </summary>
    /// <param name="buffer">已接收字节（会被就地消费/压缩）。</param>
    /// <param name="length">缓冲区中有效字节数。</param>
    /// <param name="onFrame">每切出一个完整帧回调一次。</param>
    /// <returns>buffer 中剩余（不完整帧）的字节数。</returns>
    int TryReadFrames(scoped Span<byte> buffer, int length, Action<ReadOnlySpan<byte>> onFrame);
}

/// <summary>
/// 长度前缀帧：<c>[长度(4 字节)][载荷]</c>。最通用的二进制协议切帧方式
/// （大端/小端可配；长度字段本身不计入长度）。
/// </summary>
public sealed class LengthPrefixedFrameCodec(bool bigEndian = false) : IFrameCodec
{
    private const int HeaderSize = 4;

    public int TryReadFrames(scoped Span<byte> buffer, int length, Action<ReadOnlySpan<byte>> onFrame)
    {
        var offset = 0;

        while (length - offset >= HeaderSize)
        {
            var header = buffer.Slice(offset, HeaderSize);
            var frameLength = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(header)
                : BinaryPrimitives.ReadInt32LittleEndian(header);

            if (frameLength < 0)
                throw new ProtocolException($"帧长度为负（{frameLength}）：协议不同步或长度字段损坏");

            if (length - offset - HeaderSize < frameLength)
                break;   // 载荷还没收全

            onFrame(buffer.Slice(offset + HeaderSize, frameLength));
            offset += HeaderSize + frameLength;
        }

        // 把未消费的字节移到缓冲区头部，供下次拼接
        if (offset > 0)
            buffer.Slice(offset, length - offset).CopyTo(buffer);

        return length - offset;
    }

    /// <summary>把一帧编码为"长度前缀 + 载荷"。</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload, bool bigEndian = false)
    {
        var frame = new byte[HeaderSize + payload.Length];

        if (bigEndian)
            BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        else
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);

        payload.CopyTo(frame.AsSpan(HeaderSize));

        return frame;
    }
}

/// <summary>分隔符帧：按 <see cref="Delimiter"/> 切帧（文本协议，如换行分隔的指令流）。</summary>
public sealed class DelimiterFrameCodec : IFrameCodec
{
    private readonly byte[] _delimiter;

    public DelimiterFrameCodec(byte delimiter) => _delimiter = [delimiter];

    public DelimiterFrameCodec(ReadOnlySpan<byte> delimiter) => _delimiter = delimiter.ToArray();

    /// <summary>分隔符（默认换行 0x0A）。</summary>
    public ReadOnlySpan<byte> Delimiter => _delimiter;

    public int TryReadFrames(scoped Span<byte> buffer, int length, Action<ReadOnlySpan<byte>> onFrame)
    {
        var offset = 0;

        while (true)
        {
            var index = buffer.Slice(offset, length - offset).IndexOf(_delimiter);
            if (index < 0)
                break;

            onFrame(buffer.Slice(offset, index));
            offset += index + _delimiter.Length;
        }

        if (offset > 0)
            buffer.Slice(offset, length - offset).CopyTo(buffer);

        return length - offset;
    }
}

/// <summary>定长帧：每帧固定字节数（简单的老旧硬件协议常见）。</summary>
public sealed class FixedSizeFrameCodec(int frameSize) : IFrameCodec
{
    public int FrameSize { get; } = frameSize > 0 ? frameSize : throw new ArgumentOutOfRangeException(nameof(frameSize));

    public int TryReadFrames(scoped Span<byte> buffer, int length, Action<ReadOnlySpan<byte>> onFrame)
    {
        var offset = 0;

        while (length - offset >= FrameSize)
        {
            onFrame(buffer.Slice(offset, FrameSize));
            offset += FrameSize;
        }

        if (offset > 0)
            buffer.Slice(offset, length - offset).CopyTo(buffer);

        return length - offset;
    }
}

/// <summary>协议不同步/帧损坏（与普通 IO 错误区分开，便于调用方决定是否断开连接）。</summary>
public sealed class ProtocolException(string message) : Exception(message);
