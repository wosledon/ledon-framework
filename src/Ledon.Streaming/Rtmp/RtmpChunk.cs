using System.Buffers;
using System.Buffers.Binary;

namespace Ledon.Streaming.Rtmp;

/// <summary>
/// RTMP 分块流解码器：把字节流重组成 <see cref="RtmpMessage"/>。
/// <para>
/// 分块格式（每个 Chunk）：
/// <code>
/// [Basic Header: 1-3 字节][Message Header: 0/3/7/11 字节][Extended Timestamp: 0/4][Chunk Data]
/// </code>
/// 大于 chunk size 的消息被切成多个 Chunk（后续 Chunk 用 fmt=3 且只带时间戳增量），
/// 解码器按 chunk size 累加重组——这是 RTMP 唯一复杂的地方。
/// </para>
/// <para>
/// 设计要点：
/// </para>
/// <list type="bullet">
///   <item>**重组状态按 chunk stream id 隔离**：规范允许不同 csid 的 Chunk 交错出现
///         （例如视频发一半插一个控制消息），单一缓冲区会直接错位；</item>
///   <item>**消息负载必须是独立数组**：重组缓冲跨消息复用，若把缓冲本身交出去，
///         同一批解码出的后续消息会覆盖前一条的数据（表现为命令名/流名错乱）；</item>
///   <item>**扩展时间戳**：时间戳 ≥ 0xFFFFFF 时 24 位字段写标记值，真实值占 4 字节，
///         且同一消息的每个 Chunk 都要带（规范 5.3.1.3）。</item>
/// </list>
/// </summary>
public sealed class RtmpChunkDecoder
{
    /// <summary>扩展时间戳标记：24 位字段放不下时写入 0xFFFFFF，真实值跟在后 4 字节。</summary>
    internal const uint ExtendedTimestampMarker = 0xFFFFFF;

    private readonly Dictionary<int, ChunkHeader> _headers = [];
    private readonly Dictionary<int, Reassembly> _reassembly = [];

    private int _chunkSize = 128;   // RTMP 默认 128 字节

    /// <summary>当前协商的块大小。</summary>
    public int ChunkSize => _chunkSize;

    /// <summary>处理收到的字节，切出完整消息时回调。</summary>
    /// <returns>剩余未消费字节数。</returns>
    public int Decode(scoped Span<byte> buffer, int length, Action<RtmpMessage> onMessage)
    {
        var offset = 0;

        while (offset < length)
        {
            var consumed = ReadChunk(buffer.Slice(offset, length - offset), onMessage);

            if (consumed == 0)
                break;   // 数据不足一个完整 Chunk

            offset += consumed;
        }

        if (offset > 0)
            buffer.Slice(offset, length - offset).CopyTo(buffer);

        return length - offset;
    }

    /// <summary>读取一个 Chunk；数据不足返回 0。</summary>
    private int ReadChunk(ReadOnlySpan<byte> span, Action<RtmpMessage> onMessage)
    {
        var position = 0;

        // 1) Basic Header：fmt(2) + chunk stream id(6/14/22)
        var first = span[position++];
        var format = (RtmpChunkFormat)(first >> 6);
        var streamId = first & 0x3F;

        if (streamId == 0)
        {
            if (span.Length < position + 1)
                return 0;
            streamId = 64 + span[position++];   // 两字节形式：第二字节 = csid - 64（64-319）
        }
        else if (streamId == 1)
        {
            if (span.Length < position + 2)
                return 0;
            streamId = 64 + BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(position, 2));
            position += 2;
        }

        if (!_headers.TryGetValue(streamId, out var header))
        {
            header = new ChunkHeader();
            _headers[streamId] = header;
        }

        // 2) Message Header（按 fmt 决定长度，缺失字段复用上一条）
        //    Type 0 的时间戳字段是绝对值，Type 1/2/3 是增量（规范 5.3.1.1）
        switch (format)
        {
            case RtmpChunkFormat.Type0:
                if (span.Length < position + 11)
                    return 0;
                header.TimestampDelta = ReadUInt24(span.Slice(position, 3));
                position += 3;
                header.MessageLength = (int)ReadUInt24(span.Slice(position, 3));
                position += 3;
                header.MessageType = (RtmpMessageType)span[position++];
                header.StreamId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(position, 4));
                position += 4;
                header.HasTimestamp = true;
                header.IsAbsoluteTimestamp = true;
                break;

            case RtmpChunkFormat.Type1:
                if (span.Length < position + 7)
                    return 0;
                header.TimestampDelta = ReadUInt24(span.Slice(position, 3));
                position += 3;
                header.MessageLength = (int)ReadUInt24(span.Slice(position, 3));
                position += 3;
                header.MessageType = (RtmpMessageType)span[position++];
                header.HasTimestamp = true;
                break;

            case RtmpChunkFormat.Type2:
                if (span.Length < position + 3)
                    return 0;
                header.TimestampDelta = ReadUInt24(span.Slice(position, 3));
                position += 3;
                header.HasTimestamp = true;
                break;

            case RtmpChunkFormat.Type3:
                // 无字段：全部复用
                break;
        }

        // 3) Extended Timestamp：时间戳字段为 0xFFFFFF 时，真实值在后 4 字节，
        //    且该消息的每个 Chunk 都必须带（规范 5.3.1.3）。
        //    注意：字段一旦消费就要清掉 HasTimestamp，否则同一条消息的后续
        //    Type3 续块会被误判成"没有扩展时间戳"，整条流从此错位。
        if (header.HasTimestamp)
        {
            if (header.TimestampDelta == ExtendedTimestampMarker)
            {
                if (span.Length < position + 4)
                    return 0;
                header.TimestampDelta = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(position, 4));
                position += 4;
                header.HasExtendedTimestamp = true;
            }
            else
            {
                header.HasExtendedTimestamp = false;
            }

            header.HasTimestamp = false;
        }
        else if (header.HasExtendedTimestamp)
        {
            // fmt=3 的续块：仍然要跳过 4 字节扩展时间戳（值不变）
            if (span.Length < position + 4)
                return 0;
            position += 4;
        }

        // 4) Chunk Data：最多 chunk size 字节（重组状态按 csid 隔离，支持交错）
        var reassembly = GetReassembly(streamId);
        var isNewMessage = reassembly.Length == 0;

        if (isNewMessage)
        {
            // Type 0 给绝对值，其余给增量
            header.AbsoluteTimestamp = header.IsAbsoluteTimestamp
                ? header.TimestampDelta
                : header.AbsoluteTimestamp + header.TimestampDelta;
        }

        var need = Math.Min(_chunkSize, header.MessageLength - reassembly.Length);
        if (span.Length < position + need)
            return 0;

        EnsureBuffer(reassembly, header.MessageLength);
        span.Slice(position, need).CopyTo(reassembly.Buffer.AsSpan(reassembly.Length));
        reassembly.Length += need;
        position += need;

        // 5) 消息完整 → 回调，重置累加状态
        if (reassembly.Length >= header.MessageLength)
        {
            // 重组缓冲跨消息复用：必须复制一份交出，
            // 否则同一批里后续消息会覆盖本条数据（命令名/流名错乱的根源）
            var payload = reassembly.Buffer.AsSpan(0, header.MessageLength).ToArray();

            onMessage(new RtmpMessage
            {
                MessageType = header.MessageType,
                StreamId = header.StreamId,
                TimestampMs = header.AbsoluteTimestamp,
                Payload = payload,
            });

            reassembly.Length = 0;
            header.IsAbsoluteTimestamp = false;
        }

        return position;
    }

    /// <summary>收到 SetChunkSize 控制消息时更新块大小。</summary>
    public void SetChunkSize(int chunkSize)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        _chunkSize = chunkSize;
    }

    private Reassembly GetReassembly(int streamId)
    {
        if (!_reassembly.TryGetValue(streamId, out var reassembly))
        {
            reassembly = new Reassembly();
            _reassembly[streamId] = reassembly;
        }

        return reassembly;
    }

    private static void EnsureBuffer(Reassembly reassembly, int length)
    {
        if (reassembly.Buffer.Length < length)
        {
            var buffer = reassembly.Buffer;
            Array.Resize(ref buffer, length);
            reassembly.Buffer = buffer;
        }
    }

    /// <summary>读 24 位大端整数（RTMP 时间戳/消息长度字段的宽度）。</summary>
    private static uint ReadUInt24(ReadOnlySpan<byte> source) =>
        (uint)((source[0] << 16) | (source[1] << 8) | source[2]);

    private sealed class ChunkHeader
    {
        public uint TimestampDelta { get; set; }
        public uint AbsoluteTimestamp { get; set; }
        public int MessageLength { get; set; }
        public RtmpMessageType MessageType { get; set; }
        public int StreamId { get; set; }
        public bool HasTimestamp { get; set; }
        public bool HasExtendedTimestamp { get; set; }

        /// <summary>本条 Chunk 的时间戳字段是绝对值（fmt=0）而非增量。</summary>
        public bool IsAbsoluteTimestamp { get; set; }
    }

    /// <summary>某个 csid 上正在重组的消息（长度 0 表示空闲）。</summary>
    private sealed class Reassembly
    {
        public byte[] Buffer { get; set; } = [];
        public int Length { get; set; }
    }
}

/// <summary>RTMP 分块流编码器：把消息切成 Chunk 写出。</summary>
public sealed class RtmpChunkEncoder
{
    private readonly IBufferWriter<byte> _writer;
    private int _chunkSize = 128;

    public RtmpChunkEncoder(IBufferWriter<byte> writer) => _writer = writer;

    /// <summary>协商块大小（对端会按这个切，我们也按这个切）。</summary>
    public void SetChunkSize(int chunkSize) => _chunkSize = chunkSize;

    /// <summary>
    /// 写一条完整消息（自动分块，每个后续 Chunk 用 fmt=3）。
    /// <para>
    /// 始终使用 fmt=0（完整头）：比头压缩多 8~11 字节/Chunk，但无需在对端维护
    /// 每 csid 的头状态，实现简单且不会被客户端的压缩假设反咬。视频帧按 4096
    /// 字节切块时额外开销约 0.2%，可以忽略。
    /// </para>
    /// </summary>
    public void WriteMessage(RtmpMessage message)
    {
        // csid 0/1 在 RTMP 规范里表示"基本头还有后续字节"，不能直接用；
        // 控制消息（StreamId 0）按约定落到 csid 2
        var csid = message.StreamId is 0 or 1 ? 2 : message.StreamId;
        var extended = message.TimestampMs >= RtmpChunkDecoder.ExtendedTimestampMarker;
        var basicSize = BasicHeaderSize(csid);
        var offset = 0;
        var first = true;

        while (first || offset < message.Payload.Length)
        {
            var size = Math.Min(_chunkSize, message.Payload.Length - offset);
            var headerSize = first
                ? basicSize + 11 + (extended ? 4 : 0)
                : basicSize + (extended ? 4 : 0);

            var span = _writer.GetSpan(headerSize + size);
            var position = WriteBasicHeader(span, first ? RtmpChunkFormat.Type0 : RtmpChunkFormat.Type3, csid);

            if (position != basicSize)
                throw new InvalidOperationException($"RTMP 基本头宽度不一致：预留 {basicSize} 字节，实写 {position} 字节");

            if (first)
            {
                WriteUInt24(span.Slice(position, 3), extended ? RtmpChunkDecoder.ExtendedTimestampMarker : message.TimestampMs);
                WriteUInt24(span.Slice(position + 3, 3), (uint)message.Payload.Length);
                span[position + 6] = (byte)message.MessageType;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(position + 7, 4), message.StreamId);
                position += 11;
            }

            if (extended)
            {
                // 同一消息的每个 Chunk 都要带扩展时间戳（规范 5.3.1.3）
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(position, 4), message.TimestampMs);
                position += 4;
            }

            message.Payload.AsSpan(offset, size).CopyTo(span.Slice(position, size));
            _writer.Advance(headerSize + size);

            offset += size;
            first = false;
        }
    }

    /// <summary>基本头宽度：csid 2-63 单字节，64-319 两字节，更大三字节。</summary>
    /// <remarks>csid 0/1 是保留值（表示扩展头），调用方已把它们改写成 csid 2。</remarks>
    private static int BasicHeaderSize(int csid) => csid switch
    {
        >= 2 and <= 63 => 1,
        >= 64 and <= 319 => 2,
        _ => 3,
    };

    /// <summary>写基本头，返回消息头起始位置。</summary>
    private static int WriteBasicHeader(Span<byte> destination, RtmpChunkFormat format, int csid)
    {
        destination[0] = (byte)(((byte)format << 6) | (csid >= 64 ? csid <= 319 ? 0 : 1 : csid));

        if (csid is >= 64 and <= 319)
        {
            destination[1] = (byte)(csid - 64);
            return 2;
        }

        if (csid > 319)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1, 2), (ushort)(csid - 64));
            return 3;
        }

        return 1;
    }

    /// <summary>读 24 位大端整数（RTMP 时间戳/消息长度字段的宽度）。</summary>
    private static uint ReadUInt24(ReadOnlySpan<byte> source) =>
        (uint)((source[0] << 16) | (source[1] << 8) | source[2]);

    /// <summary>写 24 位大端整数（RTMP 时间戳/消息长度字段的宽度）。</summary>
    private static void WriteUInt24(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }
}
