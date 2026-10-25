using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Streaming;
using Ledon.Streaming.Media;
using Ledon.Streaming.Playback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>
/// RTMP 端到端：真实握手 → connect → publish → 媒体写入 → HTTP-FLV 播放。
/// 用一个极简 RTMP 客户端（手写握手与分块）验证服务端行为。
/// </summary>
public class RtmpEndToEndTests : IAsyncDisposable
{
    private readonly int _rtmpPort = TestPorts.Next();
    private WebApplication? _app;

    /// <summary>测试用鉴权器：只认 <c>secret-token</c>。</summary>
    private sealed class TokenAuthenticator : Ledon.Streaming.Rtmp.IRtmpAuthenticator
    {
        public ValueTask<bool> AuthorizePublishAsync(
            Ledon.Streaming.Rtmp.RtmpAuthContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.Token == "secret-token");

        public ValueTask<bool> AuthorizePlayAsync(
            Ledon.Streaming.Rtmp.RtmpAuthContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    [Fact]
    public async Task Handshake_completes_and_publish_is_accepted()
    {
        StartStreaming();
        using var client = new MiniRtmpClient();
        await client.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        await client.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await client.SendCommandAsync("releaseStream", 2);
        await client.SendCommandAsync("FCPublish", 3);
        await client.SendCommandAsync("createStream", 4);
        await client.SendCommandAsync("publish", 5, "live/e2e");

        // 等 onStatus(NetStream.Publish.Start)
        var status = await client.WaitForStatusAsync("NetStream.Publish.Start", TimeSpan.FromSeconds(5));

        Assert.NotNull(status);
    }

    [Fact]
    public async Task Published_video_reaches_http_flv_playback()
    {
        StartStreaming();
        using var client = new MiniRtmpClient();
        await client.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        await client.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await client.SendCommandAsync("createStream", 4);
        await client.SendCommandAsync("publish", 5, "live/flvtest");
        await client.WaitForStatusAsync("NetStream.Publish.Start", TimeSpan.FromSeconds(5));

        // 推一个关键帧（序列头）
        await client.SendVideoAsync([0x17, 0, 0, 0, 0, 0xAA], timestampMs: 0);
        await client.SendVideoAsync([0x27, 1, 0, 0, 0, 0xBB], timestampMs: 33);

        // HTTP-FLV 播放：应拿到 FLV 头 + 两个 Tag
        var http = new HttpClient();

        var response = await http.GetAsync(
            $"http://127.0.0.1:{_httpPort}/live/live/flvtest.flv",
            HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();
        Assert.Equal("video/x-flv", response.Content.Headers.ContentType?.MediaType);

        var stream = await response.Content.ReadAsStreamAsync();
        var header = new byte[13];
        var read = 0;
        while (read < 13)
            read += await stream.ReadAsync(header.AsMemory(read, 13 - read));

        Assert.Equal((byte)'F', header[0]);
        Assert.Equal((byte)'L', header[1]);
        Assert.Equal((byte)'V', header[2]);
    }

    [Fact]
    public async Task Playing_unknown_stream_reports_not_found()
    {
        StartStreaming();
        using var client = new MiniRtmpClient();
        await client.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        await client.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await client.SendCommandAsync("createStream", 4);
        await client.SendCommandAsync("play", 5, "live/nope");

        var status = await client.WaitForStatusAsync("NetStream.Play.StreamNotFound", TimeSpan.FromSeconds(5));

        Assert.NotNull(status);
    }

    [Fact]
    public async Task Playing_published_stream_receives_media()
    {
        StartStreaming();
        using var publisher = new MiniRtmpClient();
        await publisher.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        await publisher.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await publisher.SendCommandAsync("createStream", 4);
        await publisher.SendCommandAsync("publish", 5, "live/relay");
        await publisher.WaitForStatusAsync("NetStream.Publish.Start", TimeSpan.FromSeconds(5));

        await publisher.SendVideoAsync([0x17, 0, 0, 0, 0, 0x11], timestampMs: 0);

        // 等服务端确实收到这一帧（否则播放者可能早于 GOP 缓存填充就订阅，拿到空流）
        var hub = _app!.Services.GetRequiredService<Ledon.Streaming.Playback.StreamHub>();
        await WaitUntilAsync(
            () => hub.Find(Ledon.Streaming.StreamIdentity.Parse("live/relay"))?.TagCount >= 1,
            TimeSpan.FromSeconds(5));

        // 另一个连接播放
        using var viewer = new MiniRtmpClient();
        await viewer.ConnectAsync(IPAddress.Loopback, _rtmpPort);
        await viewer.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await viewer.SendCommandAsync("createStream", 4);
        await viewer.SendCommandAsync("play", 5, "live/relay");

        await viewer.WaitForStatusAsync("NetStream.Play.Start", TimeSpan.FromSeconds(5));

        // 播放者应收到视频消息（GOP 补发）
        var message = await viewer.WaitForMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.Video, TimeSpan.FromSeconds(5));

        Assert.NotNull(message);
        Assert.Equal(0x17, message![0]);   // 关键帧首字节
    }

    [Fact]
    public async Task Publish_without_token_is_rejected_when_required()
    {
        StartStreaming(options => options.RequirePublishToken = true);

        using var client = new MiniRtmpClient();
        await client.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        await client.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await client.SendCommandAsync("createStream", 4);
        await client.SendCommandAsync("publish", 5, "live/relay");

        // 没有令牌：必须看到失败状态，而不是假装发布成功
        var status = await client.WaitForStatusAsync("NetStream.Failed", TimeSpan.FromSeconds(5));

        Assert.NotNull(status);
    }

    [Fact]
    public async Task Publish_with_valid_token_is_accepted()
    {
        StartStreaming(options =>
        {
            options.RequirePublishToken = true;
        });

        using var client = new MiniRtmpClient();
        await client.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        // 令牌可以放在 connect 对象里，也可以放在推流地址的查询串上
        await client.SendCommandAsync("connect", 1, new Dictionary<string, string>
        {
            ["app"] = "live",
            ["token"] = "secret-token",
        });
        await client.SendCommandAsync("createStream", 4);
        await client.SendCommandAsync("publish", 5, "live/relay?token=secret-token");

        var status = await client.WaitForStatusAsync("NetStream.Publish.Start", TimeSpan.FromSeconds(5));

        Assert.NotNull(status);
    }

    [Fact]
    public async Task Ping_request_is_answered_with_ping_response()
    {
        // 保活间隔压到 200ms：服务端应周期性发 PingRequest
        StartStreaming(options => options.PingInterval = TimeSpan.FromMilliseconds(200));

        using var client = new MiniRtmpClient();
        await client.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        await client.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await WaitUntilAsync(() => client.ReceivedAny, TimeSpan.FromSeconds(5));

        // 保活是双向的：客户端发 PingRequest，服务端必须回 PingResponse，
        // 否则对端按超时掐断我们
        await client.SendPingRequestAsync();

        var response = await client.WaitForUserControlAsync(7, TimeSpan.FromSeconds(5));   // 7 = PingResponse

        Assert.NotNull(response);
    }

    [Fact]
    public async Task Silent_connection_is_closed_by_idle_timeout()
    {
        StartStreaming(options => options.IdleTimeout = TimeSpan.FromMilliseconds(400));

        using var client = new MiniRtmpClient();
        await client.ConnectAsync(IPAddress.Loopback, _rtmpPort);

        await client.SendCommandAsync("connect", 1, new Dictionary<string, string> { ["app"] = "live" });
        await WaitUntilAsync(() => client.ReceivedAny, TimeSpan.FromSeconds(5));

        // 连上就什么都不发：空闲超时应把它踢掉，否则连接永远占着资源
        await WaitUntilAsync(() => client.Closed, TimeSpan.FromSeconds(5));

        Assert.True(client.Closed, "静默连接未被断开");
    }

    private readonly int _httpPort = TestPorts.Next();

    private void StartStreaming(Action<Ledon.Streaming.Rtmp.RtmpServerOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_httpPort}");
        builder.Services.AddLedonStreaming(o =>
        {
            o.Secret = "test-secret";
            o.PublicHttpUrl = "http://127.0.0.1";
        });
        builder.Services.Configure<Ledon.Streaming.Rtmp.RtmpServerOptions>(options =>
        {
            options.Address = "127.0.0.1";
            options.Port = _rtmpPort;
            configure?.Invoke(options);
        });
        builder.Services.AddSingleton<Ledon.Streaming.Rtmp.IRtmpAuthenticator, TokenAuthenticator>();

        builder.AddLedonWeb();
        _app = builder.Build();
        _app.UseLedonWeb();   // 映射端点（HTTP-FLV / HLS / webhook）

        // 宿主启动会一并启动 RtmpServer / HlsSegmenter（它们都是 IHostedService）
        _app.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();

        GC.SuppressFinalize(this);
    }
}

/// <summary>极简 RTMP 客户端：只实现测试需要的行为（握手 + 命令 + 媒体）。</summary>
internal sealed class MiniRtmpClient : IDisposable, IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly List<byte> _received = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NetworkStream? _stream;
    private int _chunkSize = 128;

    /// <summary>是否收到过任何字节。</summary>
    public bool ReceivedAny
    {
        get
        {
            lock (_received)
                return _received.Count > 0;
        }
    }

    /// <summary>对端是否已关闭（读循环退出）。</summary>
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
        Assert.Equal(3, s0s1[0]);

        // S2 + C2
        await _stream.WriteAsync(s0s1.AsMemory(1, 1536));   // C2 = S1 的回显
        await ReadExactAsync(1536);                        // S2

        _ = Task.Run(PumpAsync);
    }

    /// <summary>
    /// 发送命令。arg1 为 Dictionary 时写 AMF 对象（connect 的 app 等），
    /// 为字符串时写裸字符串（publish/play 的流名）。
    /// </summary>
    public async Task SendCommandAsync(string name, double transactionId, object? arg1 = null, object? arg2 = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Ledon.Streaming.Media.Amf0Writer(buffer);
        writer.WriteString(name);
        writer.WriteNumber(transactionId);
        writer.WriteNull();

        if (arg1 is Dictionary<string, string> properties)
        {
            writer.WriteObjectStart();
            foreach (var (key, value) in properties)
                writer.WriteProperty(key, value);
            writer.WriteObjectEnd();
        }
        else if (arg1 is string text)
        {
            writer.WriteString(text);
        }

        if (arg2 is string text2)
            writer.WriteString(text2);

        await SendMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.CommandAmf0, 3, 0, buffer.WrittenSpan.ToArray());
    }

    public async Task SendVideoAsync(byte[] payload, uint timestampMs) =>
        await SendMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.Video, 3, timestampMs, payload);

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
                var text = Encoding.ASCII.GetString(_received.ToArray());
                if (text.Contains(code, StringComparison.Ordinal))
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
        var span = _received.ToArray();
        var offset = 0;
        while (offset + 12 <= span.Length)
        {
            var fmt = (Ledon.Streaming.Rtmp.RtmpChunkFormat)(span[offset] >> 6);
            if (fmt != Ledon.Streaming.Rtmp.RtmpChunkFormat.Type0)
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
            // 断开（含被服务端踢掉）
            Volatile.Write(ref _closed, 1);
        }
    }

    /// <summary>写 24 位大端整数（RTMP 时间戳/长度字段宽度）。</summary>
    private static void WriteUInt24(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }

    /// <summary>发一个 PingRequest（用户控制事件 6 + 4 字节时间戳）。</summary>
    public async Task SendPingRequestAsync()
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload, 6);   // PingRequest
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(2, 4), Environment.TickCount);

        await SendMessageAsync(Ledon.Streaming.Rtmp.RtmpMessageType.UserControl, 2, 0, payload);
    }

    /// <summary>等指定类型的用户控制事件（如 7 = PingResponse）。</summary>
    public async Task<byte[]?> WaitForUserControlAsync(ushort eventType, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var message in DecodeMessages())
            {
                if (message.Type != Ledon.Streaming.Rtmp.RtmpMessageType.UserControl)
                    continue;

                if (message.Payload.Length < 2)
                    continue;

                if (BinaryPrimitives.ReadUInt16BigEndian(message.Payload) == eventType)
                    return message.Payload;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private async Task<byte[]> ReadExactAsync(int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
            read += await _stream!.ReadAsync(buffer.AsMemory(read, count - read));

        return buffer;
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
