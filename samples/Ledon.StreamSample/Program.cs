using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Streaming;
using Ledon.Streaming.Playback;
using Ledon.Streaming.Rtmp;
using Ledon.StreamSample;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ============================================================================
// Ledon.StreamSample —— 真实 RTMP 媒体面：推流 → 分发 → HTTP-FLV 播放
//
// 不依赖任何外部媒体服务器：协议栈在进程内跑。
//   OBS / ffmpeg  → rtmp://127.0.0.1:{rtmp}/live/relay   （推流）
//   浏览器 flv.js → http://127.0.0.1:{http}/live/live/relay.flv （播放）
//   Safari        → http://127.0.0.1:{http}/live/live/relay.m3u8（HLS）
//
// 示例自带一个极简 RTMP 客户端（手写握手 + 分块），推一路流再用 HTTP 拉回来，
// 端到端证明"推的出去、拉得回来"。
// ============================================================================

var rtmpPort = FreePort();
var httpPort = FreePort();

// 模块用静态端口回填（真实项目从配置读，模块里直接绑 IOptions）
StreamSampleModule.RtmpPort = rtmpPort;
StreamSampleModule.HttpPort = httpPort;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// AddLedonWeb 会一并初始化 StreamSampleModule（自动发现 + 拓扑序）
builder.AddLedonWeb();

await using var app = builder.Build();
app.UseLedonWeb();

await app.StartAsync();
await WaitPortAsync(rtmpPort);

Console.WriteLine("=== 1. RTMP 服务端已监听 ===");
Console.WriteLine($"  推流地址：rtmp://127.0.0.1:{rtmpPort}/live/relay");
Console.WriteLine($"  HTTP-FLV：http://127.0.0.1:{httpPort}/live/live/relay.flv");
Console.WriteLine($"  HLS      ：http://127.0.0.1:{httpPort}/live/live/relay.m3u8");
Console.WriteLine("  用 OBS 推流 / flv.js 播放即可，本示例继续用内置客户端跑通全链路。");

// ---- 推流 ----
Console.WriteLine();
Console.WriteLine("=== 2. 推流（内置极简 RTMP 客户端） ===");

await using var publisher = new MiniRtmpClient();
await publisher.ConnectAsync(IPAddress.Loopback, rtmpPort);

await publisher.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
await publisher.SendCommandAsync("createStream", 4);
await publisher.SendCommandAsync("publish", 5, streamName: "live/relay");

await publisher.WaitForStatusAsync("NetStream.Publish.Start", TimeSpan.FromSeconds(5));
Console.WriteLine("  connect → createStream → publish：已收到 NetStream.Publish.Start");

// 序列头（AVC sequence header）+ 一个 GOP：观众立刻能解
await publisher.SendVideoAsync([0x17, 0, 0, 0, 0, 0x01, 0x64, 0x00, 0x1F], timestampMs: 0);
await publisher.SendVideoAsync([0x17, 0, 0, 0, 0, 0x11, 0xDE, 0xAD, 0xBE, 0xEF], timestampMs: 0);
await publisher.SendVideoAsync([0x27, 1, 0, 0, 0, 0x12, 0xBA, 0xAD, 0xF0, 0x0D], timestampMs: 33);
await publisher.SendAudioAsync([0xAF, 0x00, 0x11, 0x22], timestampMs: 0);

var hub = app.Services.GetRequiredService<StreamHub>();
await WaitUntilAsync(
    () => hub.Find(StreamIdentity.Parse("live/relay"))?.TagCount >= 4,
    TimeSpan.FromSeconds(5));

var stream = hub.Find(StreamIdentity.Parse("live/relay"))!;
Console.WriteLine($"  StreamHub 已收到 {stream.TagCount} 个 Tag（{stream.TotalBytes} 字节），GOP 缓存就绪");

// ---- RTMP 拉流 ----
Console.WriteLine();
Console.WriteLine("=== 3. RTMP 拉流（另一个连接 play） ===");

await using var viewer = new MiniRtmpClient();
await viewer.ConnectAsync(IPAddress.Loopback, rtmpPort);

await viewer.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
await viewer.SendCommandAsync("createStream", 4);
await viewer.SendCommandAsync("play", 5, streamName: "live/relay");

await viewer.WaitForStatusAsync("NetStream.Play.Start", TimeSpan.FromSeconds(5));
var video = await viewer.WaitForMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.Video, TimeSpan.FromSeconds(5));

Console.WriteLine($"  收到 NetStream.Play.Start；首帧 {video?.Length ?? 0} 字节，" +
                  $"首字节 0x{video?[0]:X2}（{(video?[0] == 0x17 ? "关键帧" : "非关键帧")}）");
Console.WriteLine("  GOP 缓存补发生效：不必等下一个关键帧就出画面");

// ---- HTTP-FLV 播放 ----
Console.WriteLine();
Console.WriteLine("=== 4. HTTP-FLV 播放（HTTP 拉同一路流） ===");

using var http = new HttpClient();
var flv = await http.GetAsync(
    $"http://127.0.0.1:{httpPort}/live/live/relay.flv",
    HttpCompletionOption.ResponseHeadersRead);

flv.EnsureSuccessStatusCode();

await using var body = await flv.Content.ReadAsStreamAsync();
var header = new byte[13];
var read = 0;
while (read < 13)
    read += await body.ReadAsync(header.AsMemory(read, 13 - read));

Console.WriteLine($"  Content-Type: {flv.Content.Headers.ContentType?.MediaType}");
Console.WriteLine($"  FLV 头：{Encoding.ASCII.GetString(header, 0, 3)} " +
                  $"v{header[3]} flags=0x{header[4]:X2}（有视频 + 有音频）");

// 读两个 Tag：头(11) + 前一个 Tag 大小(4)
var tagHeader = new byte[11];
var tagRead = 0;
while (tagRead < 11)
    tagRead += await body.ReadAsync(tagHeader.AsMemory(tagRead, 11 - tagRead));

var tagType = tagHeader[0];
var dataSize = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
Console.WriteLine($"  首个 Tag：type={(Ledon.Streaming.Media.FlvTagType)tagType}，{dataSize} 字节负载");
Console.WriteLine("  同一路推流同时供 RTMP / HTTP-FLV 两种拉流");

// ---- HLS ----
Console.WriteLine();
Console.WriteLine("=== 5. HLS 播放列表 ===");

var m3u8 = await http.GetAsync($"http://127.0.0.1:{httpPort}/live/live/relay.m3u8");
Console.WriteLine($"  GET /live/live/relay.m3u8 → HTTP {(int)m3u8.StatusCode}");
Console.WriteLine("  404 = 还没有分片：示例只推了 33ms 媒体，不到一个分片时长（默认 2 秒）。");
Console.WriteLine("  持续推流几秒后再请求就会拿到 #EXTM3U 播放列表；分片由 HlsSegmenter 实时切。");

// ---- 统计与生命周期 ----
Console.WriteLine();
Console.WriteLine("=== 6. 统计与生命周期 ===");

foreach (var stats in hub.Stats())
{
    Console.WriteLine($"  流 {stats.Stream}：发布中={stats.IsPublished} 观众={stats.Subscribers} " +
                      $"Tag={stats.Tags} 字节={stats.TotalBytes} 丢帧={stats.DroppedForSlow}");
}

Console.WriteLine($"  当前 {hub.Streams.Count} 条流；停止发布后保留 " +
                  $"{new StreamHubOptions().UnpublishedRetention.TotalSeconds} 秒供重连，到期由 StreamHubSweeper 回收");

Console.WriteLine();
Console.WriteLine("示例结束（宿主停止时模块按初始化逆序关闭）。");

await app.StopAsync();

// ---------------------------------------------------------------- 辅助与类型

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task WaitPortAsync(int port)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (true)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port);
            return;
        }
        catch (SocketException)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"RTMP 端口 {port} 未就绪");
            await Task.Delay(20);
        }
    }
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition() && DateTime.UtcNow < deadline)
        await Task.Delay(20);

    if (!condition())
        throw new TimeoutException("等待条件超时");
}

/// <summary>
/// 极简 RTMP 客户端：只实现示例需要的部分（握手 + connect/createStream/publish/play + 音视频）。
/// 真实项目直接用 OBS / ffmpeg / 各平台 SDK，不需要自己写。
/// </summary>
internal sealed class MiniRtmpClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly List<byte> _received = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NetworkStream? _stream;
    private int _chunkSize = 128;

    public bool Closed => Volatile.Read(ref _closed) != 0;

    private int _closed;

    public async Task ConnectAsync(IPAddress address, int port)
    {
        await _client.ConnectAsync(address, port);
        _stream = _client.GetStream();

        // C0 + C1
        var c0c1 = new byte[1537];
        c0c1[0] = 3;
        Random.Shared.NextBytes(c0c1.AsSpan(1));
        await _stream.WriteAsync(c0c1);

        // S0 + S1
        var s0s1 = await ReadExactAsync(1537);

        // C2 = S1 回显
        await _stream.WriteAsync(s0s1.AsMemory(1, 1536));
        await ReadExactAsync(1536);   // S2

        _ = Task.Run(PumpAsync);
    }

    public async Task SendCommandAsync(string name, double transactionId, Dictionary<string, string>? properties = null, string? streamName = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Ledon.Streaming.Media.Amf0Writer(buffer);
        writer.WriteString(name);
        writer.WriteNumber(transactionId);
        writer.WriteNull();

        if (properties is not null)
        {
            writer.WriteObjectStart();
            foreach (var (key, value) in properties)
                writer.WriteProperty(key, value);
            writer.WriteObjectEnd();
        }

        if (streamName is not null)
            writer.WriteString(streamName);

        await SendMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.CommandAmf0, 3, 0, buffer.WrittenSpan.ToArray());
    }

    public Task SendVideoAsync(byte[] payload, uint timestampMs) =>
        SendMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.Video, 3, timestampMs, payload);

    public Task SendAudioAsync(byte[] payload, uint timestampMs) =>
        SendMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.Audio, 3, timestampMs, payload);

    private async Task SendMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType type, int streamId, uint timestamp, byte[] payload)
    {
        await _gate.WaitAsync();
        try
        {
            var offset = 0;
            var first = true;

            while (first || offset < payload.Length)
            {
                var size = Math.Min(_chunkSize, payload.Length - offset);
                var chunk = new byte[12 + size];

                if (first)
                {
                    chunk[0] = (byte)(((byte)Ledon.Streaming.Rtmp.RtmpChunkFormat.Type0 << 6) | 3);
                    WriteUInt24(chunk, 1, timestamp);
                    WriteUInt24(chunk, 4, (uint)payload.Length);
                    chunk[7] = (byte)type;
                    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8, 4), streamId);
                    payload.AsSpan(offset, size).CopyTo(chunk.AsSpan(12));
                }
                else
                {
                    chunk[0] = (byte)((byte)Ledon.Streaming.Rtmp.RtmpChunkFormat.Type3 << 6);
                    payload.AsSpan(offset, size).CopyTo(chunk.AsSpan(1));
                }

                await _stream!.WriteAsync(chunk.AsMemory(0, (first ? 12 : 1) + size));

                offset += size;
                first = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> WaitForStatusAsync(string code, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_received)
            {
                if (Encoding.ASCII.GetString(_received.ToArray()).Contains(code, StringComparison.Ordinal))
                    return code;
            }

            await Task.Delay(50);
        }

        return null;
    }

    public async Task<byte[]?> WaitForMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType type, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var message in DecodeMessages())
            {
                if (message.Type == type)
                    return message.Payload;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private IEnumerable<(Ledon.Streaming.Rtmp.RtmpMessageType Type, byte[] Payload)> DecodeMessages()
    {
        byte[] span;
        lock (_received)
            span = _received.ToArray();

        var offset = 0;
        while (offset + 12 <= span.Length)
        {
            if ((Ledon.Streaming.Rtmp.RtmpChunkFormat)(span[offset] >> 6) != Ledon.Streaming.Rtmp.RtmpChunkFormat.Type0)
                yield break;

            var length = (span[offset + 4] << 16) | (span[offset + 5] << 8) | span[offset + 6];
            var type = (Ledon.Streaming.Rtmp.RtmpMessageType)span[offset + 7];

            if (offset + 12 + length > span.Length)
                yield break;

            yield return (type, span.AsSpan(offset + 12, length).ToArray());
            offset += 12 + length;
        }
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await _stream!.ReadAsync(buffer);
                if (read == 0)
                    break;

                lock (_received)
                    _received.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            Volatile.Write(ref _closed, 1);
        }
        catch (Exception)
        {
            Volatile.Write(ref _closed, 1);
        }
    }

    private async Task<byte[]> ReadExactAsync(int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
            read += await _stream!.ReadAsync(buffer.AsMemory(read, count - read));

        return buffer;
    }

    private static void WriteUInt24(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _gate.Dispose();
        await Task.CompletedTask;
    }
}
