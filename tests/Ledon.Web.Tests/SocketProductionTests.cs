using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Protocols;
using Ledon.Sockets.Client;
using Ledon.Sockets.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>
/// 生产级行为：断线自动重连、并发发送不交错、空闲超时、连接数上限。
/// 这些是用真实 TCP 连接验证的——字节流错位不会抛异常，只会让对端收到乱码。
/// </summary>
public class SocketProductionTests : IAsyncLifetime
{
    private readonly int _port = TestPorts.Next();
    private readonly List<IHostedService> _services = [];
    private readonly List<TcpProtocolClient> _clients = [];
    private readonly List<UdpProtocolClient> _udpClients = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();

        foreach (var client in _udpClients)
            await client.DisposeAsync();

        foreach (var hosted in _services)
            await hosted.StopAsync(CancellationToken.None);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Client_reconnects_automatically_after_server_restart()
    {
        var recorder = new RecordingHandler();
        var echo = new EchoHandler();

        StartServer(o => o.Port = _port, echo);
        var client = CreateClient(recorder, options =>
        {
            // 缩小退避，让测试不必等默认 2 秒
            options.ReconnectDelay = TimeSpan.FromMilliseconds(100);
            options.MaxReconnectDelay = TimeSpan.FromMilliseconds(200);
        });

        await client.ConnectAsync();
        await WaitUntilAsync(() => recorder.ConnectedCount >= 1, TimeSpan.FromSeconds(5));

        // 停掉服务端：客户端应自动重连，不需要调用方再 ConnectAsync
        await StopServicesAsync();
        await WaitUntilAsync(() => recorder.DisconnectedCount >= 1, TimeSpan.FromSeconds(5));

        Assert.False(client.IsConnected);

        StartServer(o => o.Port = _port, echo);

        await WaitUntilAsync(() => recorder.ConnectedCount >= 2, TimeSpan.FromSeconds(10));

        // 重连后必须真的能收发：旧连接没人读的话，响应会全部丢掉
        await client.SendAsync(LengthPrefixedFrameCodec.Encode("after-restart"u8));
        await WaitUntilAsync(() => recorder.Frames.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal("pong:after-restart", Encoding.UTF8.GetString(recorder.Frames[0]));
    }

    [Fact]
    public async Task Concurrent_sends_are_not_interleaved()
    {
        var recorder = new RecordingHandler();
        StartServer(o => o.Port = _port, new EchoHandler());

        var client = CreateClient(recorder);
        await client.ConnectAsync();
        await WaitUntilAsync(() => recorder.ConnectedCount >= 1, TimeSpan.FromSeconds(5));

        const int tasks = 8;
        const int perTask = 40;

        // 8 个任务同时往同一条连接写：写门闩必须保证帧不交叉
        await Task.WhenAll(Enumerable.Range(0, tasks).Select(task => Task.Run(async () =>
        {
            for (var i = 0; i < perTask; i++)
            {
                var payload = Encoding.UTF8.GetBytes($"t{task}-{i}");
                await client.SendAsync(LengthPrefixedFrameCodec.Encode(payload));
            }
        })));

        // 收到的每一帧都必须是完整合法的：交错写会让长度字段和载荷错位
        await WaitUntilAsync(() => recorder.Frames.Count >= tasks * perTask, TimeSpan.FromSeconds(10));

        var received = recorder.Frames
            .Select(frame => Encoding.UTF8.GetString(frame))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(tasks * perTask, received.Count);   // 一帧都没丢/没串
    }

    [Fact]
    public async Task Silent_connection_is_closed_by_idle_timeout()
    {
        var recorder = new RecordingHandler();
        StartServer(o =>
        {
            o.Port = _port;
            o.IdleTimeout = TimeSpan.FromMilliseconds(300);
        }, new EchoHandler());

        var client = CreateClient(recorder);
        await client.ConnectAsync();
        await WaitUntilAsync(() => recorder.ConnectedCount >= 1, TimeSpan.FromSeconds(5));

        // 连上就什么都不发：空闲超时应把它踢掉（否则一条静默连接永远占用资源）
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await WaitUntilAsync(() => recorder.DisconnectedCount >= 1, TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        Assert.True(recorder.DisconnectedCount >= 1, "静默连接未被断开");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"断开得太慢（{stopwatch.ElapsedMilliseconds} ms），空闲超时可能没生效");
    }

    [Fact]
    public async Task Connections_beyond_limit_are_rejected()
    {
        StartServer(o =>
        {
            o.Port = _port;
            o.MaxConnections = 1;
        }, new EchoHandler());

        var first = CreateClient(new RecordingHandler());
        await first.ConnectAsync();
        await WaitUntilAsync(() => first.IsConnected, TimeSpan.FromSeconds(5));

        // 第二条连接：服务端应直接关掉，不进处理器、不发任何数据
        using var rejected = new TcpClient();
        await rejected.ConnectAsync(IPAddress.Loopback, _port);

        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var read = await rejected.GetStream().ReadAsync(buffer, cts.Token);

        Assert.Equal(0, read);   // 对端关闭 = 被拒绝
    }

    [Fact]
    public async Task Connection_context_exposes_stats_and_thread_safe_items()
    {
        var handler = new StatsHandler();
        StartServer(o => o.Port = _port, handler);

        var client = CreateClient(new RecordingHandler());
        await client.ConnectAsync();

        await client.SendAsync(LengthPrefixedFrameCodec.Encode("hello"u8));
        await WaitUntilAsync(() => handler.SeenStats, TimeSpan.FromSeconds(5));

        var stats = handler.Stats!;
        Assert.True(stats.ReceivedBytes > 0, "应统计收到的字节");
        Assert.True(stats.Frames >= 1, "应统计切出的帧数");
        Assert.True(handler.ItemsWrites >= 50, "并发写 Items 不应丢");
    }

    [Fact]
    public async Task Udp_dispatch_is_bounded_under_flood()
    {
        var handler = new ConcurrencyProbeHandler();
        StartUdpServer(o =>
        {
            o.Port = _port;
            o.MaxConcurrency = 4;
            o.QueueCapacity = 1000;
        }, handler);

        using var client = new UdpClient();
        var payload = new byte[32];

        for (var i = 0; i < 200; i++)
            await client.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, _port));

        await WaitUntilAsync(() => handler.Processed >= 150, TimeSpan.FromSeconds(10));

        // UDP 本身会丢包，所以不断言"全部处理完"；断言的是并发度没有失控：
        // 洪水数据报每个都起一个任务会把线程池打爆
        Assert.True(handler.PeakConcurrency <= 4,
            $"并发处理数达到 {handler.PeakConcurrency}，超过配置上限 4");
    }

    [Fact]
    public async Task Udp_client_binds_the_requested_local_port()
    {
        // SIP 要把本地端口写进 Via/Contact：必须能固定端口，不能用随机 ephemeral
        var localPort = TestPorts.Next();

        var client = new UdpProtocolClient(
            new ProtocolClientOptions
            {
                Host = "127.0.0.1",
                Port = _port,
                LocalPort = localPort,
            },
            new RecordingHandler());

        _udpClients.Add(client);

        await client.ConnectAsync();

        Assert.Equal(localPort, client.LocalPort);
    }

    private void StartUdpServer(Action<UdpServerOptions> configure, IUdpHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSocketUdpServer(configure, handler);

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Add(hosted);
        }
    }

    private void StartServer(Action<TcpServerOptions> configure, IConnectionHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSocketServer(configure, new LengthPrefixedFrameCodec(), handler);

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Add(hosted);
        }

        // BackgroundService.StartAsync 立即返回：等端口真的能连接再往下走
        WaitUntilPortAcceptsAsync(_port, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private async Task StopServicesAsync()
    {
        foreach (var hosted in _services)
            await hosted.StopAsync(CancellationToken.None);

        _services.Clear();
    }

    private TcpProtocolClient CreateClient(IClientHandler handler, Action<ProtocolClientOptions>? configure = null)
    {
        var options = new ProtocolClientOptions { Host = "127.0.0.1", Port = _port };
        configure?.Invoke(options);

        var client = new TcpProtocolClient(
            options,
            new LengthPrefixedFrameCodec(),
            handler,
            NullLogger<TcpProtocolClient>.Instance);

        _clients.Add(client);

        return client;
    }

    private static async Task WaitUntilPortAcceptsAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

                return;
            }
            catch (SocketException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException($"服务端在 {timeout.TotalSeconds} 秒内仍未监听 {port}");

                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private sealed class RecordingHandler : ClientHandlerBase
    {
        private readonly List<byte[]> _frames = [];
        private readonly Lock _gate = new();
        private int _connected;
        private int _disconnected;

        public IReadOnlyList<byte[]> Frames
        {
            get
            {
                lock (_gate)
                    return _frames.ToArray();
            }
        }

        public int ConnectedCount => Volatile.Read(ref _connected);
        public int DisconnectedCount => Volatile.Read(ref _disconnected);
        public Exception? LastException { get; private set; }

        public override Task OnConnectedAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connected);
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception, CancellationToken cancellationToken = default)
        {
            LastException = exception;
            Interlocked.Increment(ref _disconnected);
            return Task.CompletedTask;
        }

        public override Task OnFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _frames.Add(frame.ToArray());

            return Task.CompletedTask;
        }
    }

    private sealed class EchoHandler : ConnectionHandlerBase
    {
        public override async Task OnFrameAsync(
            ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            await connection.SendAsync(LengthPrefixedFrameCodec.Encode(
                Encoding.UTF8.GetBytes($"pong:{Encoding.UTF8.GetString(frame.Span)}")), cancellationToken);
        }
    }

    /// <summary>记录连接统计与 Items 并发写入的处理器。</summary>
    private sealed class StatsHandler : ConnectionHandlerBase    {
        private int _itemsWrites;

        public ConnectionStats? Stats { get; private set; }
        public bool SeenStats { get; private set; }
        public int ItemsWrites => Volatile.Read(ref _itemsWrites);

        public override async Task OnFrameAsync(
            ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            // 并发写 Items：普通 Dictionary 在这里会抛/丢数据
            var writes = Enumerable.Range(0, 50)
                .Select(i => Task.Run(() => connection.Items[$"key-{i % 5}"] = i, CancellationToken.None))
                .ToArray();

            await Task.WhenAll(writes).ConfigureAwait(false);

            Interlocked.Add(ref _itemsWrites, writes.Length);

            Stats = connection.Stats;
            SeenStats = true;

            await connection.SendAsync(LengthPrefixedFrameCodec.Encode("ok"u8), cancellationToken);
        }
    }

    /// <summary>记录并发峰值与处理数的 UDP 处理器。</summary>
    private sealed class ConcurrencyProbeHandler : IUdpHandler
    {
        private int _active;
        private int _peak;
        private int _processed;

        public int Processed => Volatile.Read(ref _processed);
        public int PeakConcurrency => Volatile.Read(ref _peak);

        public async Task OnDatagramAsync(UdpContext datagram, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);

            // 记录峰值（CAS 循环，避免漏记）
            var peak = Volatile.Read(ref _peak);
            while (active > peak && active != (peak = Interlocked.CompareExchange(ref _peak, active, peak)))
            {
                peak = Volatile.Read(ref _peak);
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);

            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _processed);
        }
    }
}