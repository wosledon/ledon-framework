using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ledon.Protocols;
using Ledon.Sockets.Client;
using Ledon.Sockets.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>客户端 ↔ 服务端联调：真实连接、类型化收发、断线重连。</summary>
public class ProtocolClientServerTests : IAsyncDisposable
{
    private readonly TestPortBlock _ports = TestPorts.Reserve();
    private int _port => _ports[0];
    private readonly List<IHostedService> _services = [];
    private readonly List<TcpProtocolClient> _clients = [];

    [Fact]
    public async Task Client_connects_and_receives_echo()
    {
        var recorder = new RecordingHandler();
        StartServer(new LengthPrefixedFrameCodec(), new EchoHandler());

        var client = CreateClient(new LengthPrefixedFrameCodec(), recorder);
        await client.ConnectAsync();

        await WaitUntilAsync(() => recorder.Connected, TimeSpan.FromSeconds(5));

        await client.SendAsync(LengthPrefixedFrameCodec.Encode("ping"u8));

        await WaitUntilAsync(() => recorder.Frames.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal("pong:ping", Encoding.UTF8.GetString(recorder.Frames[0]));
    }

    [Fact]
    public async Task Client_survives_server_restart_and_reconnects()
    {
        var recorder = new RecordingHandler();
        var handler = new EchoHandler();
        StartServer(new LengthPrefixedFrameCodec(), handler);

        var client = CreateClient(new LengthPrefixedFrameCodec(), recorder);
        await client.ConnectAsync();
        await WaitUntilAsync(() => recorder.Connected, TimeSpan.FromSeconds(5));

        // 停掉服务端：客户端应观察到断开
        await StopServicesAsync();

        await WaitUntilAsync(() => recorder.Disconnected, TimeSpan.FromSeconds(5));
        Assert.False(client.IsConnected);

        // 重新起服务端（同端口）：客户端手动重连后继续工作
        StartServer(new LengthPrefixedFrameCodec(), handler);
        await client.ConnectAsync();

        await WaitUntilAsync(() => recorder.Connected, TimeSpan.FromSeconds(10));

        await client.SendAsync(LengthPrefixedFrameCodec.Encode("again"u8));
        await WaitUntilAsync(() => recorder.Frames.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal("pong:again", Encoding.UTF8.GetString(recorder.Frames[0]));
    }

    [Fact]
    public async Task Server_sends_multiple_frames_in_order()
    {
        var recorder = new RecordingHandler();
        StartServer(new LengthPrefixedFrameCodec(), new GreetHandler());

        var client = CreateClient(new LengthPrefixedFrameCodec(), recorder);
        await client.ConnectAsync();
        await WaitUntilAsync(() => recorder.Connected, TimeSpan.FromSeconds(5));

        await client.SendAsync(LengthPrefixedFrameCodec.Encode("hi"u8));

        await WaitUntilAsync(() => recorder.Frames.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal("welcome", Encoding.UTF8.GetString(recorder.Frames[0]));
        Assert.Equal("ack:hi", Encoding.UTF8.GetString(recorder.Frames[1]));
    }

    [Fact]
    public async Task Delimiter_codec_works_end_to_end()
    {
        var recorder = new RecordingHandler();
        StartServer(new DelimiterFrameCodec((byte)'\n'), new LineEchoHandler());

        var client = CreateClient(new DelimiterFrameCodec((byte)'\n'), recorder);
        await client.ConnectAsync();
        await WaitUntilAsync(() => recorder.Connected, TimeSpan.FromSeconds(5));

        await client.SendAsync(Encoding.UTF8.GetBytes("hello\n"));

        await WaitUntilAsync(() => recorder.Frames.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal("line:hello", Encoding.UTF8.GetString(recorder.Frames[0]).TrimEnd('\n'));
    }

    [Fact]
    public async Task Sending_without_connecting_fails_fast()
    {
        var client = CreateClient(new LengthPrefixedFrameCodec(), new RecordingHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(LengthPrefixedFrameCodec.Encode("x"u8)));
    }

    private void StartServer(IFrameCodec codec, IConnectionHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSocketServer(o => o.Port = _port, codec, handler);

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Add(hosted);
        }

        // BackgroundService.StartAsync 立即返回，ExecuteAsync 在别的线程上跑：
        // 端口绑定/监听有延迟，不等待就直接连会撞上 "Connection refused"
        WaitUntilPortAcceptsAsync(_port, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    /// <summary>等待端口真正可连接（探测连接成功后由服务端自行处理空闲连接）。</summary>
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

    private TcpProtocolClient CreateClient(IFrameCodec codec, IClientHandler handler)
    {
        var client = new TcpProtocolClient(
            new ProtocolClientOptions { Host = "127.0.0.1", Port = _port },
            codec,
            handler,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TcpProtocolClient>.Instance);

        _clients.Add(client);

        return client;
    }

    private async Task StopServicesAsync()
    {
        foreach (var hosted in _services)
            await hosted.StopAsync(CancellationToken.None);

        _services.Clear();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    public async ValueTask DisposeAsync()
    {
        await StopServicesAsync();

        foreach (var client in _clients)
            await client.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    private sealed class RecordingHandler : ClientHandlerBase
    {
        private readonly List<byte[]> _frames = [];
        private int _connected;
        private int _disconnected;

        public IReadOnlyList<byte[]> Frames
        {
            get
            {
                lock (_frames)
                    return _frames.ToArray();
            }
        }

        public bool Connected => Volatile.Read(ref _connected) > 0;

        public bool Disconnected => Volatile.Read(ref _disconnected) > 0;

        public override Task OnConnectedAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connected);
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _disconnected);
            return Task.CompletedTask;
        }

        public override Task OnFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            lock (_frames)
                _frames.Add(frame.ToArray());

            return Task.CompletedTask;
        }
    }

    private sealed class EchoHandler : ConnectionHandlerBase
    {
        public override async Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(frame.Span);

            await connection.SendAsync(
                LengthPrefixedFrameCodec.Encode(Encoding.UTF8.GetBytes($"pong:{text}")),
                cancellationToken);
        }
    }

    private sealed class GreetHandler : ConnectionHandlerBase
    {
        public override async Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(frame.Span);

            await connection.SendAsync(LengthPrefixedFrameCodec.Encode("welcome"u8), cancellationToken);
            await connection.SendAsync(LengthPrefixedFrameCodec.Encode(Encoding.UTF8.GetBytes($"ack:{text}")), cancellationToken);
        }
    }

    private sealed class LineEchoHandler : ConnectionHandlerBase
    {
        public override async Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            var line = Encoding.UTF8.GetString(frame.Span).TrimEnd('\r');

            await connection.SendAsync(Encoding.UTF8.GetBytes($"line:{line}\n"), cancellationToken);
        }
    }
}
