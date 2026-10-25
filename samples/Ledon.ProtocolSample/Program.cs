using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Protocols;
using Ledon.Sip;
using Ledon.Sip.Client;
using Ledon.Sip.Server;
using Ledon.Sockets.Client;
using Ledon.Sockets.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ============================================================================
// Ledon.ProtocolSample —— 协议栈示例：结构体映射 → 帧切分 → TCP/UDP 收发 → SIP
//
// 四个层次各自独立、可单独使用：
//   Ledon.Protocols      字节流 → 帧 → 结构体（布局即契约，无需 IDL）
//   Ledon.Sockets.Server 监听 / 连接管理 / 类型化分发
//   Ledon.Sockets.Client 连接 / 断线重连 / 类型化收发
//   Ledon.Sip            SIP 解析 + 应答 + 事务（构建在 Socket 库之上）
// ============================================================================

var tcpPort = FreePort();
var sipPort = FreePort();

var services = new ServiceCollection();
services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddSimpleConsole(o => o.SingleLine = true));

// ---- 服务端：长度前缀帧 + 类型化分发（处理器按约定扫描注册） ----------------
services.AddLedonSocketServer(
    o => { o.Port = tcpPort; o.IdleTimeout = TimeSpan.FromSeconds(30); },
    new LengthPrefixedFrameCodec(),
    new TypedConnectionHandler(new IPacketDispatcher[] { new TypeIdPacketDispatcher() }));

// 扫描带 [PacketType] 的包与其 IPacketHandler<> 实现（编译成委托，之后零反射）。
// 包的全局字节序是大端（[PacketField(BigEndian = true)] 的字段无论全局设置如何都按大端读）
services.AddLedonPacketHandlers(bigEndian: true);

// ---- SIP 服务端：UDP 监听 + 应答（OPTIONS 自动回 200 OK） --------------------
services.AddLedonSipServer(o => { o.Port = sipPort; o.EnableTcp = false; });
services.AddSingleton<ISipHandler, SampleSipHandler>();

await using var provider = services.BuildServiceProvider();

foreach (var hosted in provider.GetServices<IHostedService>())
    await hosted.StartAsync(CancellationToken.None);

// 等端口真的能连接再往下走（BackgroundService.StartAsync 是立即返回的）
await WaitPortAsync(tcpPort);
await WaitUdpAsync(sipPort);

Console.WriteLine("=== 1. 结构体映射：一份结构体就是协议契约 ===");

// 两端各写一份结构体就能通信：布局在 [PacketField] 上声明，运行时编译成读写委托
var ping = new PingRequest { Version = 2, User = "ledon", Nonce = 0x5A5A5A5A, Urgent = true };
var wire = PacketSerializer<PingRequest>.Serialize(ping, bigEndian: true);

Console.WriteLine($"  PingRequest 序列化 {wire.Length} 字节；" +
                  $"定长={PacketSerializer<PingRequest>.IsFixedSize}，" +
                  $"字段数={PacketSerializer<PingRequest>.FieldCount}");
var echoed = PacketSerializer<PingRequest>.Deserialize(wire, bigEndian: true);
Console.WriteLine($"  反序列化 → Version={echoed.Version} User={echoed.User} Nonce=0x{echoed.Nonce:X8} Urgent={echoed.Urgent}");

// 纯定长布局时大小编译期已知，可以直接进栈缓冲区（热路径零分配）
var telemetry = new Telemetry { Temperature = 36.6, Humidity = 41, Flags = 0b101 };
var telemetryWire = PacketSerializer<Telemetry>.Serialize(telemetry, bigEndian: true);
Console.WriteLine($"  Telemetry（全定长）→ {telemetryWire.Length} 字节，Size={PacketSerializer<Telemetry>.Size}，" +
                  $"Measure 预分配 {PacketSerializer<Telemetry>.Measure(telemetry)} 字节");

Console.WriteLine();
Console.WriteLine("=== 2. 帧编解码：字节流 → 完整帧 ===");

var codec = new LengthPrefixedFrameCodec();
var payload = Encoding.UTF8.GetBytes("hello");
var framed = LengthPrefixedFrameCodec.Encode(payload);   // [长度(4)][载荷]

// 故意拆成两半喂：验证半包拼接（真实网络里这才是常态）
var frames = new List<byte[]>();
var buffer = new byte[64];
var split = 5;   // 只够放进长度字段 + 1 字节载荷

framed.AsSpan(0, split).CopyTo(buffer);
var remaining = codec.TryReadFrames(buffer, split, f => frames.Add(f.ToArray()));
Console.WriteLine($"  喂前 {split} 字节 → 切出 {frames.Count} 帧，剩余 {remaining} 字节待拼接");

framed.AsSpan(split).CopyTo(buffer.AsSpan(remaining));
remaining = codec.TryReadFrames(buffer, remaining + framed.Length - split, f => frames.Add(f.ToArray()));
Console.WriteLine($"  补齐后 → 切出 {frames.Count} 帧，内容 \"{Encoding.UTF8.GetString(frames[0])}\"，剩余 {remaining} 字节");

Console.WriteLine();
Console.WriteLine("=== 3. TCP 服务端 + 客户端（真实连接、类型化收发、断线重连） ===");

var echo = new RecordingHandler();
await using var client = new TcpProtocolClient(
    new ProtocolClientOptions
    {
        Host = "127.0.0.1",
        Port = tcpPort,
        AutoReconnect = true,
        ReconnectDelay = TimeSpan.FromMilliseconds(200),
        MaxReconnectDelay = TimeSpan.FromSeconds(2),
    },
    new LengthPrefixedFrameCodec(),
    echo,
    NullLogger<TcpProtocolClient>.Instance);

await client.ConnectAsync();
Console.WriteLine($"  连接成功：IsConnected={client.IsConnected}");
await WaitUntilAsync(() => echo.Connected, TimeSpan.FromSeconds(5));

// 帧 = [长度前缀][类型标识(2)][结构体]：长度前缀由帧编解码负责，里面才是协议内容
var frame = new byte[2 + wire.Length];
BinaryPrimitives.WriteUInt16LittleEndian(frame, PingRequest.TypeId);
wire.CopyTo(frame.AsSpan(2));

await client.SendAsync(LengthPrefixedFrameCodec.Encode(frame));
await WaitUntilAsync(() => echo.Frames.Count > 0, TimeSpan.FromSeconds(5));
Console.WriteLine($"  已连接，发送 {frame.Length} 字节 → 收到回显 {echo.Frames[0].Length} 字节");
Console.WriteLine($"  已发送类型化帧（[类型ID 2 字节][定长结构体]），等待回显");

Console.WriteLine();
Console.WriteLine("=== 4. SIP over UDP：真实报文往返 ===");

await using var sip = new SipClient(
    new SipClientOptions
    {
        Host = "127.0.0.1",
        Port = sipPort,
        Transport = SipTransport.Udp,
        RetryInterval = TimeSpan.FromMilliseconds(200),
        TransactionTimeout = TimeSpan.FromSeconds(5),
    },
    NullLogger<SipClient>.Instance);

await sip.ConnectAsync();
Console.WriteLine($"  SIP 客户端本地端口 {sip.LocalPort}（写进 Via/Contact）");

// 只要方法和 URI：Via/branch/Call-ID/CSeq/Contact 全部自动生成
var response = await sip.SendAsync("OPTIONS", $"sip:ledon@127.0.0.1:{sipPort}");
Console.WriteLine($"  OPTIONS → {response.StatusCode} {response.ReasonPhrase}");
Console.WriteLine($"  To tag = {response.ToAddress?.Tag}（UAS 必须补，否则真实 UAC 判为非法）");
Console.WriteLine($"  Via branch = {response.TopVia?.Branch}");
Console.WriteLine($"  Contact = {response.Contact}");

// 解析一条真实 INVITE，展示类型化头
var inviteText = """
    INVITE sip:bob@example.com SIP/2.0
    Via: SIP/2.0/UDP proxy.example.com:5060;branch=z9hG4bK-abc
    From: "Alice" <sip:alice@example.com>;tag=9
    To: <sip:bob@example.com>
    Call-ID: demo@example.com
    CSeq: 1 INVITE
    Content-Length: 0

    """;
var invite = SipParser.Parse(inviteText);
Console.WriteLine($"  解析 INVITE → 方法={invite.Method} From.tag={invite.FromAddress?.Tag} " +
                  $"From.user={invite.FromAddress?.User} CSeq={invite.CSeqValue}");

// 应答构造器：自动补 To tag / Contact / Server / Content-Length
var ok = SipResponder.Ok(invite, "Ledon/1.0");
Console.WriteLine($"  应答 200 OK 自动补全：To tag={ok.ToAddress?.Tag?[..12]}... Contact={ok.Contact}");

Console.WriteLine();
Console.WriteLine("=== 5. 断线重连（指数退避 + 抖动） ===");

Console.WriteLine("  连接由单一监管循环持有：断开后按 ReconnectDelay 起步、封顶 MaxReconnectDelay 重试，");
Console.WriteLine("  每次成功都触发 OnConnectedAsync（业务可以在这里做登录握手）");

await client.CloseAsync();
Console.WriteLine($"  主动关闭后 IsConnected={client.IsConnected}");
await client.ConnectAsync();
await WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(5));
Console.WriteLine("  重新连接成功（自动重连不丢句柄，业务无感）");

foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
    await hosted.StopAsync(CancellationToken.None);

Console.WriteLine();
Console.WriteLine("示例结束。");

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
                throw new TimeoutException($"端口 {port} 未就绪");
            await Task.Delay(20);
        }
    }
}

static async Task WaitUdpAsync(int port)
{
    // UDP 没有"可连接"概念：发一个探测包，等到回包即说明服务端在跑
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var probe = SipParser.CreateRequest("OPTIONS", "sip:probe@127.0.0.1", new Dictionary<string, string>
    {
        ["Via"] = $"SIP/2.0/UDP 127.0.0.1;branch=z9hG4bK-{Guid.NewGuid():n}",
        ["From"] = "<sip:probe@127.0.0.1>;tag=1",
        ["To"] = "<sip:probe@127.0.0.1>",
        ["Call-ID"] = $"probe-{Guid.NewGuid():n}@127.0.0.1",
        ["CSeq"] = "1 OPTIONS",
    }).ToBytes();

    var endpoint = new IPEndPoint(IPAddress.Loopback, port);
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

    while (true)
    {
        await socket.SendToAsync(probe, endpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            var buffer = new byte[4096];
            var received = await socket.ReceiveFromAsync(buffer, endpoint, cts.Token);
            if (received.ReceivedBytes > 0)
                return;
        }
        catch (OperationCanceledException)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"UDP {port} 未就绪");
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

/// <summary>示例协议包：定长布局，大端（网络字节序）。</summary>
[PacketType(PingRequest.TypeId)]
public struct PingRequest
{
    /// <summary>线上类型标识（帧的前两个字节）。</summary>
    public const ushort TypeId = 1;

    public PingRequest()
    {
    }

    [PacketField(0, PacketFieldKind.UInt32, BigEndian = true)]
    public uint Version;

    [PacketField(1, PacketFieldKind.Utf8String, BigEndian = true)]
    public string User = "";

    [PacketField(2, PacketFieldKind.UInt32, BigEndian = true)]
    public uint Nonce;

    [PacketField(3, PacketFieldKind.Bool)]
    public bool Urgent;
}

/// <summary>全定长布局：大小编译期已知，热路径可以直接进栈缓冲区。</summary>
public struct Telemetry
{
    [PacketField(0, PacketFieldKind.Double, BigEndian = true)]
    public double Temperature;

    [PacketField(1, PacketFieldKind.UInt16, BigEndian = true)]
    public ushort Humidity;

    [PacketField(2, PacketFieldKind.UInt8)]
    public byte Flags;
}

/// <summary>回显处理器：收到什么就回什么（展示类型化分发）。</summary>
public sealed class EchoPacketHandler : IPacketHandler<PingRequest>
{
    public Task HandleAsync(ConnectionContext connection, PingRequest packet, CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        PacketSerializer<PingRequest>.Write(packet, buffer, bigEndian: true);

        var payload = new byte[2 + buffer.WrittenCount];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, PingRequest.TypeId);
        buffer.WrittenSpan.CopyTo(payload.AsSpan(2));

        // ConnectionContext.SendAsync 发的是**已组帧**的字节：帧编解码那一层要自己套
        return connection.SendAsync(LengthPrefixedFrameCodec.Encode(payload), cancellationToken);
    }
}

/// <summary>把类型化分发器接到连接事件上（业务侧 10 行胶水）。</summary>
public sealed class TypedConnectionHandler(IPacketDispatcher[] dispatchers) : ConnectionHandlerBase
{
    public override async Task OnFrameAsync(
        ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        foreach (var dispatcher in dispatchers)
            await dispatcher.DispatchAsync(connection, frame, cancellationToken);
    }
}

/// <summary>示例 SIP 处理器：OPTIONS 回 200，其余回 405。</summary>
public sealed class SampleSipHandler : ISipHandler
{
    public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult<SipMessage?>(message.Method switch
        {
            "OPTIONS" => SipResponder.Ok(message, "Ledon/1.0"),
            "INVITE" => SipResponder.Response(message, 405, "Method Not Allowed"),
            _ => null,
        });
}

/// <summary>记录客户端事件的处理器。</summary>
public sealed class RecordingHandler : ClientHandlerBase
{
    private readonly List<byte[]> _frames = [];
    private int _connected;

    public IReadOnlyList<byte[]> Frames
    {
        get { lock (_frames) return _frames.ToArray(); }
    }

    public bool Connected => Volatile.Read(ref _connected) != 0;

    public override Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connected);
        return Task.CompletedTask;
    }

    public override Task OnFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        lock (_frames) _frames.Add(frame.ToArray());
        return Task.CompletedTask;
    }
}
