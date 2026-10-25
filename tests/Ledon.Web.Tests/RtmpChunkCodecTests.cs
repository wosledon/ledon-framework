using System.Buffers;
using System.Buffers.Binary;
using Ledon.Streaming.Rtmp;

namespace Ledon.Web.Tests;

/// <summary>
/// RTMP 分块流编解码器：分块重组、扩展时间戳、csid 交错、负载归属。
/// 这些用例对应真实踩过的坑——字节流错位不会抛异常，只会让命令名变成乱码。
/// </summary>
public class RtmpChunkCodecTests
{
    [Fact]
    public void Large_chunk_stream_id_uses_extended_basic_header()
    {
        // csid ≥ 64 要走两字节基本头（第二字节 = csid - 64），不能按单字节截断
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);

        var decoded = Roundtrip(
            [new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 100, TimestampMs = 7, Payload = payload }],
            chunkSize: 4096);

        var message = Assert.Single(decoded);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(100, message.StreamId);
        Assert.Equal(7u, message.TimestampMs);
    }

    /// <summary>一条 37 字节的 connect 命令（真实客户端布局）。</summary>
    private static readonly byte[] ConnectPayload =
    [
        0x02, 0x00, 0x07, (byte)'c', (byte)'o', (byte)'n', (byte)'n', (byte)'e', (byte)'c', (byte)'t',
        0x00, 0x3F, 0xF0, 0, 0, 0, 0, 0, 0,
        0x05,
        0x30, 0x00, 0x03, (byte)'a', (byte)'p', (byte)'p', 0x02, 0x00, 0x04,
        (byte)'l', (byte)'i', (byte)'v', (byte)'e', 0x00, 0x00, 0x09,
    ];

    [Fact]
    public void Encoder_decoder_roundtrip_preserves_every_message()
    {
        var messages = new[]
        {
            new RtmpMessage { MessageType = RtmpMessageType.WindowAcknowledgementSize, StreamId = 0, TimestampMs = 0, Payload = [0x00, 0x26, 0x25, 0xA0] },
            new RtmpMessage { MessageType = RtmpMessageType.SetChunkSize, StreamId = 0, TimestampMs = 0, Payload = [0x00, 0x00, 0x10, 0x00] },
            new RtmpMessage { MessageType = RtmpMessageType.CommandAmf0, StreamId = 3, TimestampMs = 0, Payload = ConnectPayload },
            new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 33, Payload = [0x17, 0, 0, 0, 0, 0x11] },
        };

        var decoded = Roundtrip(messages, chunkSize: 4096);

        Assert.Equal(4, decoded.Count);
        Assert.Equal(RtmpMessageType.WindowAcknowledgementSize, decoded[0].MessageType);
        Assert.Equal(ConnectPayload, decoded[2].Payload);
        Assert.Equal(33u, decoded[3].TimestampMs);
        Assert.Equal(new byte[] { 0x17, 0, 0, 0, 0, 0x11 }, decoded[3].Payload);
    }

    /// <summary>
    /// 回归：同一批解码出的多条消息必须各自持有独立负载。
    /// 重组缓冲是复用的，把缓冲本身交出去会让后一条覆盖前一条
    /// （症状：connect 命令的负载变成后面 play 命令的字节）。
    /// </summary>
    [Fact]
    public void Messages_decoded_in_one_batch_keep_their_own_payloads()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var encoder = new RtmpChunkEncoder(buffer);
        encoder.SetChunkSize(4096);

        var createStream = new RtmpMessage
        {
            MessageType = RtmpMessageType.CommandAmf0,
            StreamId = 3,
            TimestampMs = 0,
            Payload = [0x02, 0x00, 0x0C, (byte)'c', (byte)'r', (byte)'e', (byte)'a', (byte)'t', (byte)'e', (byte)'S', (byte)'t', (byte)'r', (byte)'e', (byte)'a', (byte)'m', 0x00, 0x40, 0x10, 0, 0, 0, 0, 0, 0, 0x05],
        };

        var play = new RtmpMessage
        {
            MessageType = RtmpMessageType.CommandAmf0,
            StreamId = 3,
            TimestampMs = 0,
            Payload = [0x02, 0x00, 0x04, (byte)'p', (byte)'l', (byte)'a', (byte)'y', 0x00, 0x40, 0x14, 0, 0, 0, 0, 0, 0, 0x05, 0x02, 0x00, 0x0A, (byte)'l', (byte)'i', (byte)'v', (byte)'e', (byte)'/', (byte)'r', (byte)'e', (byte)'l', (byte)'a', (byte)'y'],
        };

        // 三条命令连续写入，模拟"一个 TCP 读包内到达多条命令"
        encoder.WriteMessage(new RtmpMessage { MessageType = RtmpMessageType.CommandAmf0, StreamId = 3, TimestampMs = 0, Payload = ConnectPayload });
        encoder.WriteMessage(createStream);
        encoder.WriteMessage(play);

        var decoded = DecodeAll(buffer.WrittenSpan, chunkSize: 4096);

        Assert.Equal(3, decoded.Count);
        Assert.Equal(ConnectPayload, decoded[0].Payload);
        Assert.Equal(createStream.Payload, decoded[1].Payload);
        Assert.Equal(play.Payload, decoded[2].Payload);
    }

    [Fact]
    public void Payload_larger_than_chunk_size_is_reassembled()
    {
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);

        var decoded = Roundtrip(
            [new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 100, Payload = payload }],
            chunkSize: 128);

        var message = Assert.Single(decoded);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(100u, message.TimestampMs);
    }

    [Fact]
    public void Extended_timestamp_roundtrips()
    {
        // 时间戳超过 24 位：字段写 0xFFFFFF，真实值占 4 字节，且每个 Chunk 都要带
        var payload = new byte[300];
        Random.Shared.NextBytes(payload);

        var decoded = Roundtrip(
            [new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 0x01_02_03_04, Payload = payload }],
            chunkSize: 128);

        var message = Assert.Single(decoded);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(0x01_02_03_04u, message.TimestampMs);
    }

    [Fact]
    public void Interleaved_chunk_streams_are_reassembled_independently()
    {
        // 规范允许不同 csid 的 Chunk 交错（视频发一半插一个控制消息）
        var video = new byte[300];
        var control = new byte[] { 0x00, 0x26, 0x25, 0xA0 };

        for (var i = 0; i < video.Length; i++)
            video[i] = (byte)(i % 251);

        var buffer = new ArrayBufferWriter<byte>();
        var encoder = new RtmpChunkEncoder(buffer);
        encoder.SetChunkSize(128);

        encoder.WriteMessage(new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 0, Payload = video });
        encoder.WriteMessage(new RtmpMessage { MessageType = RtmpMessageType.WindowAcknowledgementSize, StreamId = 0, TimestampMs = 0, Payload = control });

        var decoded = DecodeAll(buffer.WrittenSpan, chunkSize: 128);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(video, decoded[0].Payload);
        Assert.Equal(control, decoded[1].Payload);
    }

    [Fact]
    public void Partial_chunk_is_buffered_until_complete()
    {
        var payload = new byte[200];
        Random.Shared.NextBytes(payload);

        var buffer = new ArrayBufferWriter<byte>();
        var encoder = new RtmpChunkEncoder(buffer);
        encoder.SetChunkSize(128);
        encoder.WriteMessage(new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 0, Payload = payload });

        var wire = buffer.WrittenSpan.ToArray();
        var decoder = new RtmpChunkDecoder();
        var pending = new byte[wire.Length];
        var messages = new List<RtmpMessage>();
        var remaining = 0;
        var produced = 0;

        // 逐字节喂入：只有最后一个字节到达时才交出完整消息。
        // 注意 Decode 会把未消费字节搬到缓冲头部，新数据要跟在剩余长度后面写
        for (var i = 0; i < wire.Length; i++)
        {
            pending[remaining++] = wire[i];
            messages.Clear();
            remaining = decoder.Decode(pending, remaining, messages.Add);

            if (i < wire.Length - 1)
            {
                Assert.Empty(messages);
            }
            else
            {
                produced = messages.Count;
            }
        }

        Assert.Equal(1, produced);
        Assert.Equal(payload, Assert.Single(messages).Payload);
    }

    [Fact]
    public void Timestamps_accumulate_across_messages_on_same_chunk_stream()
    {
        var decoded = Roundtrip(
            [
                new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 0, Payload = [1] },
                new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 33, Payload = [2] },
                new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 66, Payload = [3] },
            ],
            chunkSize: 4096);

        Assert.Equal([0u, 33u, 66u], decoded.Select(m => m.TimestampMs));
    }

    [Fact]
    public void Compressed_headers_from_a_real_client_are_decoded()
    {
        // 手写 fmt=1/2 的 chunk 流：只有第一条带完整头（绝对时间戳），
        // 后续只带时间增量（Type 1 还带长度+类型，Type 2 只带增量）
        var buffer = new ArrayBufferWriter<byte>();
        var encoder = new RtmpChunkEncoder(buffer);
        encoder.SetChunkSize(4096);
        encoder.WriteMessage(new RtmpMessage { MessageType = RtmpMessageType.CommandAmf0, StreamId = 3, TimestampMs = 0, Payload = ConnectPayload });
        encoder.WriteMessage(new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 40, Payload = [0x17, 0, 0, 0, 0, 1] });
        encoder.WriteMessage(new RtmpMessage { MessageType = RtmpMessageType.Video, StreamId = 3, TimestampMs = 73, Payload = [0x27, 1, 0, 0, 0, 2] });

        var compressed = CompressHeaders(buffer.WrittenSpan.ToArray());

        var decoded = DecodeAll(compressed, chunkSize: 4096);

        Assert.Equal(3, decoded.Count);
        Assert.Equal(ConnectPayload, decoded[0].Payload);
        Assert.Equal(new byte[] { 0x17, 0, 0, 0, 0, 1 }, decoded[1].Payload);
        Assert.Equal(new byte[] { 0x27, 1, 0, 0, 0, 2 }, decoded[2].Payload);
        Assert.Equal(0u, decoded[0].TimestampMs);
        Assert.Equal(40u, decoded[1].TimestampMs);
        Assert.Equal(73u, decoded[2].TimestampMs);
    }

    /// <summary>把连续的同 csid 消息改写成压缩头（第二条 fmt=1，第三条 fmt=2）。</summary>
    private static byte[] CompressHeaders(byte[] wire)
    {
        var result = new List<byte>();
        var offset = 0;
        var index = 0;
        var previousTimestamp = 0u;

        while (offset + 12 <= wire.Length)
        {
            var csid = wire[offset] & 0x3F;
            var timestamp = ((uint)wire[offset + 1] << 16) | ((uint)wire[offset + 2] << 8) | wire[offset + 3];
            var length = (wire[offset + 4] << 16) | (wire[offset + 5] << 8) | wire[offset + 6];
            var total = 12 + length;

            if (offset + total > wire.Length)
                break;

            if (index == 0)
            {
                result.AddRange(wire.AsSpan(offset, total));
            }
            else if (index == 1)
            {
                // fmt=1：时间增量(3) + 长度(3) + 类型(1)，复用流 ID
                result.Add((byte)((1 << 6) | csid));
                WriteUInt24(result, timestamp - previousTimestamp);
                result.AddRange(wire.AsSpan(offset + 4, 4));     // 长度(3) + 类型(1)
                result.AddRange(wire.AsSpan(offset + 12, length));
            }
            else
            {
                // fmt=2：只有时间增量(3)
                result.Add((byte)((2 << 6) | csid));
                WriteUInt24(result, timestamp - previousTimestamp);
                result.AddRange(wire.AsSpan(offset + 12, length));
            }

            previousTimestamp = timestamp;
            offset += total;
            index++;
        }

        result.AddRange(wire.AsSpan(offset));

        return [.. result];
    }

    private static void WriteUInt24(List<byte> destination, uint value)
    {
        destination.Add((byte)(value >> 16));
        destination.Add((byte)(value >> 8));
        destination.Add((byte)value);
    }

    private static List<RtmpMessage> Roundtrip(RtmpMessage[] messages, int chunkSize)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var encoder = new RtmpChunkEncoder(buffer);
        encoder.SetChunkSize(chunkSize);

        foreach (var message in messages)
            encoder.WriteMessage(message);

        return DecodeAll(buffer.WrittenSpan, chunkSize);
    }

    private static List<RtmpMessage> DecodeAll(ReadOnlySpan<byte> wire, int chunkSize)
    {
        var decoder = new RtmpChunkDecoder();
        decoder.SetChunkSize(chunkSize);

        var buffer = wire.ToArray();
        var messages = new List<RtmpMessage>();
        var remaining = decoder.Decode(buffer, buffer.Length, messages.Add);

        Assert.Equal(0, remaining);

        return messages;
    }
}
