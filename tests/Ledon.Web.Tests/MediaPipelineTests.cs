using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Streaming;
using Ledon.Streaming.Hls;
using Ledon.Streaming.Media;
using Ledon.Streaming.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>媒体面：FLV 封装、AMF0、TS 复用、流枢纽。</summary>
public class MediaPipelineTests
{
    [Fact]
    public void Flv_header_has_signature_and_flags()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Flv.WriteHeader(buffer);

        var bytes = buffer.WrittenSpan;

        Assert.Equal((byte)'F', bytes[0]);
        Assert.Equal((byte)'L', bytes[1]);
        Assert.Equal((byte)'V', bytes[2]);
        Assert.Equal(1, bytes[3]);
        Assert.Equal(0b101, bytes[4]);           // 有视频 + 有音频
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(5, 4)));
        Assert.Equal(13, bytes.Length);
    }

    [Fact]
    public void Flv_tag_roundtrips_with_previous_tag_size()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Flv.WriteTag(buffer, new FlvTag
        {
            TagType = FlvTagType.Video,
            TimestampMs = 0x0102_0304,
            Data = [1, 2, 3],
        });

        var bytes = buffer.WrittenSpan;

        Assert.Equal((byte)FlvTagType.Video, bytes[0]);
        Assert.Equal(3, (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);   // 24 位长度
        // UI24（大端）在 4-6，ExtendedTimestamp 在 7
        Assert.Equal(0x02, bytes[4]);
        Assert.Equal(0x03, bytes[5]);
        Assert.Equal(0x04, bytes[6]);
        Assert.Equal(0x01, bytes[7]);
        Assert.Equal(1, bytes[11]);
        Assert.Equal(2, bytes[12]);
        Assert.Equal(3, bytes[13]);
        Assert.Equal(14u, BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(14, 4)));   // PreviousTagSize
    }

    [Fact]
    public void Flv_keyframe_detection()
    {
        var key = new FlvTag { TagType = FlvTagType.Video, TimestampMs = 0, Data = [0x17, 0, 0, 0, 0] };
        var inter = new FlvTag { TagType = FlvTagType.Video, TimestampMs = 33, Data = [0x27, 0, 0, 0, 0] };
        var audio = new FlvTag { TagType = FlvTagType.Audio, TimestampMs = 0, Data = [0xAF, 0] };

        Assert.True(key.IsKeyFrame);
        Assert.False(inter.IsKeyFrame);
        Assert.False(audio.IsKeyFrame);
    }

    [Fact]
    public void Amf0_roundtrips_command_and_object()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Amf0Writer(buffer);
        writer.WriteString("connect");
        writer.WriteNumber(1);
        writer.WriteObjectStart();
        writer.WriteProperty("app", "live");
        writer.WriteProperty("objectEncoding", 0);
        writer.WriteObjectEnd();

        var reader = new Amf0Reader(buffer.WrittenSpan);
        Assert.Equal("connect", reader.ReadValue());   // 带 0x02 marker 的字符串值
        Assert.Equal(1d, reader.ReadValue());

        var obj = Assert.IsType<Dictionary<string, object?>>(reader.ReadValue());
        Assert.Equal("live", obj["app"]);
        Assert.Equal(0d, obj["objectEncoding"]);
    }

    [Fact]
    public void Amf0_roundtrips_ecma_array()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Amf0Writer(buffer);
        writer.WriteEcmaArrayStart(1);
        writer.WriteProperty("code", "NetStream.Play.Start");
        writer.WriteObjectEnd();

        var reader = new Amf0Reader(buffer.WrittenSpan);
        var obj = Assert.IsType<Dictionary<string, object?>>(reader.ReadValue());

        Assert.Equal("NetStream.Play.Start", obj["code"]);
    }

    [Fact]
    public void Ts_muxer_emits_pat_pmt_and_188_byte_packets()
    {
        var muxer = new TsMuxer();
        var buffer = new ArrayBufferWriter<byte>();

        muxer.Write(buffer, new FlvTag
        {
            TagType = FlvTagType.Video,
            TimestampMs = 0,
            Data = [0x17, 0, 0, 0, 0, 1, 2, 3, 4],   // 关键帧 + AVC 头 + 4 字节负载
        });

        var bytes = buffer.WrittenSpan;

        Assert.True(bytes.Length >= TsMuxer.TsPacketSize * 3);
        Assert.Equal(0x47, bytes[0]);                          // PAT 同步字节
        Assert.Equal(TsMuxer.TsPacketSize * 3, bytes.Length);
        Assert.True(bytes.Length % TsMuxer.TsPacketSize == 0);

        // 头部 2 字节：PUSI(0x4000) + PID(低 13 位）
        var pmtHeader = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(TsMuxer.TsPacketSize + 1, 2));
        Assert.Equal(0x1000, pmtHeader & 0x1FFF);
        Assert.Equal(0x4000, pmtHeader & 0x4000);
    }

    [Fact]
    public void Ts_packets_are_all_188_bytes_with_sync_byte()
    {
        var muxer = new TsMuxer();
        var buffer = new ArrayBufferWriter<byte>();

        for (var i = 0; i < 10; i++)
        {
            muxer.Write(buffer, new FlvTag
            {
                TagType = FlvTagType.Video,
                TimestampMs = (uint)(i * 33),
                Data = [0x27, 0, 0, 0, 0, .. Enumerable.Range(0, 50).Select(x => (byte)x)],
            });
        }

        var bytes = buffer.WrittenSpan;
        Assert.Equal(0, bytes.Length % TsMuxer.TsPacketSize);

        for (var offset = 0; offset < bytes.Length; offset += TsMuxer.TsPacketSize)
            Assert.Equal(0x47, bytes[offset]);   // 每个包都以同步字节开头
    }

    [Fact]
    public async Task Stream_hub_publishes_writes_and_broadcasts()
    {
        var hub = new StreamHub(NullLogger<StreamHub>.Instance);
        var identity = StreamIdentity.Parse("live/test");

        Assert.Null(hub.Subscribe(identity));   // 未发布：订阅失败

        hub.Publish(identity, "pub-1");
        var subscription = hub.Subscribe(identity);
        Assert.NotNull(subscription);

        hub.Write(identity, new FlvTag
        {
            TagType = FlvTagType.Video,
            TimestampMs = 0,
            Data = [0x17, 0, 0, 0, 0, 9],
        });

        var tag = await ReadOneAsync(subscription!.Value);

        Assert.True(tag.IsKeyFrame);
        Assert.Equal((byte)9, tag.Data[^1]);
    }

    [Fact]
    public async Task New_subscriber_gets_gop_cache_immediately()
    {
        var hub = new StreamHub(NullLogger<StreamHub>.Instance);
        var identity = StreamIdentity.Parse("live/gop");

        hub.Publish(identity);

        // 序列头 + 一个 GOP 的帧（FLV video tag：byte0=帧类型<<4|codec，byte1=AVCPacketType）
        hub.Write(identity, Tag(FlvTagType.Video, 0, [0x17, 0, 0, 0, 0, 1]));     // 关键帧 + 序列头
        hub.Write(identity, Tag(FlvTagType.Video, 33, [0x27, 1, 0, 0, 0, 2]));    // 帧间帧（AVCPacketType=1）

        // 后加入的订阅者应立即拿到 GOP（不必等下一个关键帧）
        var late = hub.Subscribe(identity)!;

        Assert.True((await ReadOneAsync(late.Value)).IsKeyFrame);
        Assert.Equal((byte)2, (await ReadOneAsync(late.Value)).Data[^1]);
    }

    [Fact]
    public void Unpublish_stops_new_subscriptions()
    {
        var hub = new StreamHub(NullLogger<StreamHub>.Instance);
        var identity = StreamIdentity.Parse("live/stop");

        hub.Publish(identity);
        Assert.NotNull(hub.Subscribe(identity));

        hub.Unpublish(identity);

        Assert.Null(hub.Subscribe(identity));
    }

    [Fact]
    public void Stream_stats_track_publishers_and_viewers()
    {
        var hub = new StreamHub(NullLogger<StreamHub>.Instance);
        var identity = StreamIdentity.Parse("live/stats");

        hub.Publish(identity, "pub-1");
        var a = hub.Subscribe(identity)!;
        var b = hub.Subscribe(identity)!;

        hub.Write(identity, Tag(FlvTagType.Video, 0, [0x17, 0, 0, 0, 0, 1]));

        var stats = Assert.Single(hub.Stats());

        Assert.True(stats.IsPublished);
        Assert.Equal(2, stats.Subscribers);
        Assert.Equal(1, stats.Tags);
        Assert.NotNull(stats.PublishedAtUtc);

        hub.Unsubscribe(identity, a.Value.Id);
        hub.Unsubscribe(identity, b.Value.Id);
    }

    [Fact]
    public void Slow_subscriber_does_not_block_publisher()
    {
        var hub = new StreamHub(NullLogger<StreamHub>.Instance);
        var identity = StreamIdentity.Parse("live/slow");

        hub.Publish(identity);
        var slow = hub.Subscribe(identity)!;   // 从不读取

        // 写入远超队列容量：发布者不应被阻塞（背压 = 丢帧）
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
            hub.Write(identity, Tag(FlvTagType.Video, (uint)i, [0x27, 0, 0, 0, 0, (byte)i]));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"发布被慢订阅者阻塞：{stopwatch.ElapsedMilliseconds} ms");
    }

    private static FlvTag Tag(FlvTagType type, uint timestamp, byte[] data) => new()
    {
        TagType = type,
        TimestampMs = timestamp,
        Data = data,
    };

    private static async Task<FlvTag> ReadOneAsync(StreamSubscription subscription)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        return await subscription.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
    }
}
