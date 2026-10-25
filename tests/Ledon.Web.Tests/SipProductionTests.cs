using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Sip;
using Ledon.Sip.Client;
using Ledon.Sip.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>
/// SIP 协议层与事务层：To tag、obs-fold、多值 Via、ACK、响应重传、TCP 传输。
/// 用真实 UDP/TCP 报文验证——字段缺失不会抛异常，只会让真实 UA 判为非法。
/// </summary>
public class SipProductionTests : IAsyncDisposable
{
    private readonly int _port = TestPorts.Next();
    private readonly List<IHostedService> _services = [];
    private readonly List<SipClient> _clients = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();

        foreach (var hosted in _services)
            await hosted.StopAsync(CancellationToken.None);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Response_adds_a_to_tag()
    {
        StartServer(new AutoOptionsHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        var request = Options("sip:test@127.0.0.1");
        var response = await client.SendRequestAsync(request);

        // UAS 必须自己生成 To tag：真实 UAC 收到没有 tag 的 To 会判为非法响应
        var to = response.ToAddress;
        Assert.NotNull(to);
        Assert.False(string.IsNullOrEmpty(to!.Tag), "响应缺少 To tag");
        Assert.NotNull(response.Header("Contact"));
        Assert.NotNull(response.Header("Server"));
    }

    [Fact]
    public async Task Client_fills_missing_transaction_headers()
    {
        StartServer(new AutoOptionsHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        // 调用方只给方法和 URI：Via/branch/Call-ID/CSeq/Contact 都该自动补上
        var response = await client.SendAsync("OPTIONS", "sip:test@127.0.0.1");

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void Parser_unfolds_continuation_lines()
    {
        // obs-fold（规范 7.3.1）：以空格开头的行是上一条头的延续
        var text = "OPTIONS sip:test@127.0.0.1 SIP/2.0\r\n" +
                   "Via: SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK-1;\r\n" +
                   " received=10.0.0.2\r\n" +
                   "From: <sip:a@10.0.0.1>;tag=1\r\n" +
                   "To: <sip:b@10.0.0.2>\r\n" +
                   "Call-ID: fold@10.0.0.1\r\n" +
                   "CSeq: 1 OPTIONS\r\n" +
                   "Content-Length: 0\r\n\r\n";

        var message = SipParser.Parse(text);

        // 折行必须拼回一条，且 branch 仍能取到
        Assert.Equal("SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK-1; received=10.0.0.2", message.Via);
        Assert.Equal("z9hG4bK-1", message.TopVia?.Branch);
        Assert.Equal("10.0.0.2", message.TopVia?.Received);
        Assert.Equal("fold@10.0.0.1", message.CallId);
    }

    [Fact]
    public void Parser_keeps_multiple_via_headers_in_order()
    {
        var text = "OPTIONS sip:test@127.0.0.1 SIP/2.0\r\n" +
                   "Via: SIP/2.0/UDP proxy2.example.com;branch=z9hG4bK-2\r\n" +
                   "Via: SIP/2.0/UDP proxy1.example.com;branch=z9hG4bK-1\r\n" +
                   "From: <sip:a@example.com>;tag=1\r\n" +
                   "To: <sip:b@example.com>\r\n" +
                   "Call-ID: multi@example.com\r\n" +
                   "CSeq: 1 OPTIONS\r\n" +
                   "Content-Length: 0\r\n\r\n";

        var message = SipParser.Parse(text);

        var vias = message.HeaderValues("Via");

        Assert.Equal(2, vias.Count);
        Assert.Contains("z9hG4bK-2", vias[0], StringComparison.Ordinal);   // 顺序不能丢
        Assert.Contains("z9hG4bK-1", vias[1], StringComparison.Ordinal);
        Assert.Equal("z9hG4bK-2", message.TopVia?.Branch);   // 最上层是第一条
    }

    [Fact]
    public void Message_roundtrips_through_wire_format()
    {
        const string body = "v=0\r\no=- 1 1 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n";

        var request = SipParser.CreateRequest("INVITE", "sip:bob@example.com", new Dictionary<string, string>
        {
            ["Via"] = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK-abc",
            ["From"] = "<sip:alice@example.com>;tag=9",
            ["To"] = "<sip:bob@example.com>",
            ["Call-ID"] = "roundtrip@example.com",
            ["CSeq"] = "1 INVITE",
            ["Contact"] = "<sip:alice@10.0.0.1>",
        }, body: body);

        var wire = Encoding.UTF8.GetString(request.ToBytes());

        // CRLF 行尾 + Content-Length 自动按 body 字节数算
        Assert.Contains("\r\n", wire, StringComparison.Ordinal);
        Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount(body)}", wire, StringComparison.Ordinal);

        var parsed = SipParser.Parse(wire);

        Assert.Equal("INVITE", parsed.Method);
        Assert.Equal("sip:bob@example.com", parsed.RequestUri);
        Assert.Equal("9", parsed.FromAddress?.Tag);
        Assert.Equal("1", parsed.CSeqValue?.Number.ToString());
        Assert.Equal("INVITE", parsed.CSeqValue?.Method);
        Assert.Equal("alice", parsed.FromAddress?.User);
        Assert.Equal("example.com", parsed.FromAddress?.Host);
        Assert.Equal(body, parsed.Body);
    }

    [Fact]
    public void Missing_required_header_is_rejected()
    {
        var text = "OPTIONS sip:test@127.0.0.1 SIP/2.0\r\n" +
                   "From: <sip:a@example.com>;tag=1\r\n" +
                   "To: <sip:b@example.com>\r\n" +
                   "CSeq: 1 OPTIONS\r\n\r\n";

        // 缺 Via：没法回包，必须早失败而不是静默通过
        Assert.Throws<SipParseException>(() => SipParser.Parse(text));
    }

    [Fact]
    public void Oversized_message_is_rejected()
    {
        var original = SipParser.MaxMessageSize;

        try
        {
            SipParser.MaxMessageSize = 64;

            var text = "OPTIONS sip:test@127.0.0.1 SIP/2.0\r\n" +
                       "Via: SIP/2.0/UDP 10.0.0.1;branch=z9hG4bK-1\r\n" +
                       "From: <sip:a@example.com>;tag=1\r\n" +
                       "To: <sip:b@example.com>\r\n" +
                       "Call-ID: big@example.com\r\n" +
                       "CSeq: 1 OPTIONS\r\n\r\n";

            Assert.Throws<SipParseException>(() => SipParser.Parse(text));
        }
        finally
        {
            SipParser.MaxMessageSize = original;
        }
    }

    [Fact]
    public async Task Duplicated_request_gets_the_same_response_without_reinvoking_the_handler()
    {
        var handler = new CountingHandler();
        StartServer(handler);

        // 用裸 UDP  socket 观察线上到底回了几条响应
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var request = Options("sip:test@127.0.0.1");
        var payload = request.ToBytes();
        var endpoint = new IPEndPoint(IPAddress.Loopback, _port);

        // 同一个 branch 连发两次：客户端没收到响应时就是这么重传的
        await socket.SendToAsync(payload, endpoint);
        await socket.SendToAsync(payload, endpoint);

        var responses = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        try
        {
            while (responses < 2)
            {
                var buffer = new byte[4096];
                var result = await socket.ReceiveMessageFromAsync(buffer, endpoint, cts.Token);
                Assert.Equal(200, SipParser.Parse(buffer.AsSpan(0, result.ReceivedBytes)).StatusCode);
                responses++;
            }
        }
        catch (OperationCanceledException)
        {
            // 超时：靠下面的断言报出来
        }

        Assert.Equal(2, responses);          // 重传的请求也拿到了响应
        Assert.Equal(1, handler.Invocations);   // 业务只跑一次
    }

    [Fact]
    public async Task Ack_ends_the_invite_transaction()
    {
        var handler = new AckProbeHandler();
        StartServer(handler);

        var client = CreateClient();
        await client.ConnectAsync();

        // 显式构造 INVITE：ACK 必须沿用同一个 Via branch，否则匹配不上事务
        var request = client.Build("INVITE", "sip:test@127.0.0.1", Sdp);
        var response = await client.SendAsync(request);

        Assert.Equal(200, response.StatusCode);

        await client.AckAsync(request, response);

        await WaitUntilAsync(() => handler.AckSeen, TimeSpan.FromSeconds(6));
        Assert.True(handler.AckSeen, "服务端没看到 ACK");
    }

    [Fact]
    public async Task Non_2xx_invite_is_acknowledged_by_the_client()
    {
        StartServer(new BusyHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        var (dialog, response) = await client.InviteAsync("sip:test@127.0.0.1", Sdp);

        Assert.Equal(486, response.StatusCode);
        Assert.Null(dialog);   // 非 2xx 不成对话

        // 规范 17.1.1.2：非 2xx 的 INVITE 客户端必须回 ACK，否则对端事务挂到 Timer H
        await WaitUntilAsync(() => BusyHandler.AckSeen, TimeSpan.FromSeconds(6));
        Assert.True(BusyHandler.AckSeen, "客户端没有为非 2xx INVITE 补 ACK");
    }

    [Fact]
    public async Task Cancel_is_accepted_for_a_pending_invite()
    {
        StartServer(new SlowHandler());

        var client = CreateClient();
        await client.ConnectAsync();

        var request = client.Build("INVITE", "sip:test@127.0.0.1", Sdp);

        // INVITE 只会拿到 100 Trying：事务最终超时
        var invite = client.SendAsync(request, TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => SlowHandler.InviteSeen, TimeSpan.FromSeconds(6));

        await client.CancelAsync(request);

        await WaitUntilAsync(() => SlowHandler.CancelSeen, TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<TimeoutException>(() => invite);
        Assert.True(SlowHandler.CancelSeen, "CANCEL 没到服务端");
    }

    [Fact]
    public async Task Tcp_transport_works()
    {
        StartServer(new AutoOptionsHandler());

        var client = CreateClient(transport: SipTransport.Tcp);
        await client.ConnectAsync();

        var response = await client.SendAsync("OPTIONS", "sip:test@127.0.0.1");

        Assert.Equal(200, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(response.ToAddress?.Tag));
    }

    private const string Sdp = "v=0\r\no=- 1 1 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n";

    private void StartServer(ISipHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSipServer(o =>
        {
            o.Port = _port;
            o.TcpPort = _port;
        });
        services.AddSingleton(handler);

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Add(hosted);
        }
    }

    private SipClient CreateClient(SipTransport transport = SipTransport.Udp)
    {
        var client = new SipClient(
            new SipClientOptions
            {
                Host = "127.0.0.1",
                Port = _port,
                Transport = transport,
                RetryInterval = TimeSpan.FromMilliseconds(100),
                TransactionTimeout = TimeSpan.FromSeconds(5),
            },
            NullLogger<SipClient>.Instance);

        _clients.Add(client);

        return client;
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private sealed class AutoOptionsHandler : ISipHandler
    {
        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult<SipMessage?>(SipResponder.Ok(message, "Ledon/1.0"));
    }

    private sealed class BusyHandler : ISipHandler
    {
        public static bool AckSeen { get; private set; }

        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default)
        {
            if (message.Method == "ACK")
            {
                AckSeen = true;
                return Task.FromResult<SipMessage?>(null);
            }

            return Task.FromResult<SipMessage?>(SipResponder.Response(message, 486, "Busy Here"));
        }
    }

    private sealed class CountingHandler : ISipHandler
    {
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocations);

            return Task.FromResult<SipMessage?>(SipResponder.Ok(message, "Ledon/1.0"));
        }
    }

    private sealed class AckProbeHandler : ISipHandler
    {
        public bool AckSeen { get; private set; }

        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default)
        {
            if (message.Method == "ACK")
            {
                AckSeen = true;
                return Task.FromResult<SipMessage?>(null);
            }

            return Task.FromResult<SipMessage?>(SipResponder.Ok(message, "Ledon/1.0"));
        }
    }

    private sealed class SlowHandler : ISipHandler
    {
        public static bool InviteSeen { get; private set; }
        public static bool CancelSeen { get; private set; }

        public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default)
        {
            switch (message.Method)
            {
                case "INVITE":
                    InviteSeen = true;
                    return Task.FromResult<SipMessage?>(SipResponder.Response(message, 100, "Trying"));

                case "CANCEL":
                    CancelSeen = true;
                    return Task.FromResult<SipMessage?>(SipResponder.Response(message, 200, "OK"));

                default:
                    return Task.FromResult<SipMessage?>(null);
            }
        }
    }
}
