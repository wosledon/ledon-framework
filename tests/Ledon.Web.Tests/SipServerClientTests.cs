using System.Net;
using System.Text;
using Ledon.Sip;
using Ledon.Sip.Client;
using Ledon.Sip.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>SIP Server/Client 联调：基于 Socket 库的真实 UDP 收发。</summary>
public class SipServerClientTests : IAsyncDisposable
{
    private readonly TestPortBlock _ports = TestPorts.Reserve();
    private int _port => _ports[0];
    private readonly List<IHostedService> _services = [];
    private readonly List<SipClient> _clients = [];

    [Fact]
    public async Task Options_is_answered_with_200_ok()
    {
        StartServer(new AutoOptionsHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        var response = await client.SendRequestAsync(Options("sip:test@127.0.0.1"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("OK", response.ReasonPhrase);
        Assert.NotNull(response.Header("User-Agent"));
    }

    [Fact]
    public async Task Response_carries_request_transaction_headers()
    {
        StartServer(new AutoOptionsHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        var request = Options("sip:test@127.0.0.1");
        var response = await client.SendRequestAsync(request);

        // 事务匹配正确时，响应的 Via/From/To/Call-ID/CSeq 应与请求一致
        Assert.Equal(request.CallId, response.CallId);
        Assert.Equal(request.CSeq, response.CSeq);
        Assert.Contains(request.From!, response.From!);
    }

    [Fact]
    public async Task Custom_handler_returns_busy()
    {
        StartServer(new BusyHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        var response = await client.SendRequestAsync(Options("sip:test@127.0.0.1"));

        Assert.Equal(486, response.StatusCode);
    }

    [Fact]
    public async Task No_response_times_out()
    {
        StartServer(new SilentHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        // 服务端收了但不应答：事务超时应抛 TimeoutException，而不是永久挂住
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SendRequestAsync(Options("sip:test@127.0.0.1"), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Malformed_datagram_does_not_kill_the_server()
    {
        var handler = new AutoOptionsHandler();
        StartServer(handler);

        var client = CreateClient();
        await client.ConnectAsync();

        // 先发垃圾数据：服务端只记录警告，继续服务
        await client.ConnectAsync();
        await SendRawAsync("这不是 SIP 报文");
        await Task.Delay(100);

        var response = await client.SendRequestAsync(Options("sip:test@127.0.0.1"));
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Tcp_frame_codec_splits_by_content_length()
    {
        var codec = new SipTcpFrameCodec();

        var first = SipResponder.Ok(Options("sip:test@127.0.0.1"), "Ledon/1.0").ToBytes();
        var second = SipResponder.Ok(Options("sip:other@127.0.0.1"), "Ledon/1.0").ToBytes();

        var wire = first.Concat(second).ToArray();
        var buffer = new byte[wire.Length];
        wire.CopyTo(buffer, 0);

        var frames = new List<byte[]>();
        var remaining = codec.TryReadFrames(buffer, wire.Length, frame => frames.Add(frame.ToArray()));

        Assert.Equal(2, frames.Count);
        Assert.Equal(0, remaining);
        Assert.Contains("Call-ID", Encoding.UTF8.GetString(frames[0]));
    }

    [Fact]
    public async Task Tcp_frame_codec_waits_for_incomplete_body()
    {
        var codec = new SipTcpFrameCodec();
        var full = SipResponder.Ok(Options("sip:test@127.0.0.1"), "Ledon/1.0").ToBytes();

        var frames = new List<byte[]>();
        var remaining = codec.TryReadFrames(full.AsSpan(0, full.Length - 5).ToArray(), full.Length - 5, frame => frames.Add(frame.ToArray()));

        Assert.Empty(frames);      // body 没收全：一帧都不切
        Assert.Equal(full.Length - 5, remaining);
    }

    private void StartServer(ISipHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(handler);
        services.AddLedonSipServer(o => { o.Address = "127.0.0.1"; o.Port = _port; });

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Add(hosted);
        }
    }

    private SipClient CreateClient()
    {
        var client = new SipClient(
            new SipClientOptions { Host = "127.0.0.1", Port = _port, TransactionTimeout = TimeSpan.FromSeconds(3) },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SipClient>.Instance);

        _clients.Add(client);

        return client;
    }

    private async Task SendRawAsync(string text)
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);

        await socket.SendToAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
            System.Net.Sockets.SocketFlags.None,
            new IPEndPoint(IPAddress.Loopback, _port));
    }

    private static SipMessage Options(string requestUri) =>
        SipParser.CreateRequest("OPTIONS", requestUri, new Dictionary<string, string>
        {
            ["Via"] = "SIP/2.0/UDP 127.0.0.1;branch=z9hG4bK-" + Guid.NewGuid().ToString("N"),
            ["From"] = "<sip:test@127.0.0.1>;tag=1",
            ["To"] = "<sip:test@127.0.0.1>",
            ["Call-ID"] = Guid.NewGuid().ToString("N") + "@127.0.0.1",
            ["CSeq"] = "1 OPTIONS",
        });

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _services)
            await hosted.StopAsync(CancellationToken.None);

        foreach (var client in _clients)
            await client.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    private sealed class AutoOptionsHandler : ISipHandler
    {
        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult<SipMessage?>(message.Method == "OPTIONS" ? SipResponder.Ok(message, "Ledon/1.0") : null);
    }

    private sealed class BusyHandler : ISipHandler
    {
        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult<SipMessage?>(SipParser.CreateResponse(486, "Busy Here", new Dictionary<string, string>
            {
                ["Via"] = message.Via ?? "",
                ["From"] = message.From ?? "",
                ["To"] = message.To ?? "",
                ["Call-ID"] = message.CallId ?? "",
                ["CSeq"] = message.CSeq ?? "",
                ["Content-Length"] = "0",
            }));
    }

    private sealed class SilentHandler : ISipHandler
    {
        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult<SipMessage?>(null);
    }
}
