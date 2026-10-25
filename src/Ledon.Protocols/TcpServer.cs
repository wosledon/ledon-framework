using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Protocols;

/// <summary>TCP 服务端选项。</summary>
public sealed class TcpServerOptions
{
    /// <summary>监听地址，默认 <c>0.0.0.0</c>。</summary>
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>监听端口。</summary>
    public int Port { get; set; }

    /// <summary>单次读取缓冲区，默认 8KB。</summary>
    public int BufferSize { get; set; } = 8192;

    /// <summary>积压队列，默认 128。</summary>
    public int Backlog { get; set; } = 128;
}

/// <summary>
/// TCP 服务端：监听 → 接受连接 → 按 <see cref="IFrameCodec"/> 切帧 → 分发给
/// <see cref="IConnectionHandler"/>。每连接一个独立读取循环，互不阻塞；
/// 帧按到达顺序处理（协议语义要求顺序），单个帧处理失败只记录不断开；
/// 协议错误（<see cref="ProtocolException"/>）断开该连接，不影响其他连接。
/// </summary>
public sealed class TcpServer : BackgroundService
{
    private readonly TcpServerOptions _options;
    private readonly IFrameCodec _codec;
    private readonly IConnectionHandler _handler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TcpServer> _logger;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, ConnectionContext> _connections = new();

    private int _connectionSeq;

    public TcpServer(
        TcpServerOptions options,
        IFrameCodec codec,
        IConnectionHandler handler,
        IServiceScopeFactory scopeFactory,
        ILogger<TcpServer> logger)
    {
        _options = options;
        _codec = codec;
        _handler = handler;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _listener = new TcpListener(IPAddress.Parse(options.Address), options.Port);
    }

    /// <summary>当前连接数。</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>当前全部连接（运维/广播用）。</summary>
    public IReadOnlyCollection<ConnectionContext> Connections => _connections.Values.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start(_options.Backlog);
        _logger.LogInformation("TCP 服务端已监听 {Address}:{Port}", _options.Address, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);

                // 每连接一个任务：一个慢连接不会拖垮其他连接
                _ = Task.Run(() => ServeAsync(client, stoppingToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主持停：正常退出
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var id = $"tcp-{Interlocked.Increment(ref _connectionSeq)}";
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        await using var scope = _scopeFactory.CreateAsyncScope();
        var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();

        var connection = new ConnectionContext(
            id,
            remote,
            (data, ct) => SendAsync(client, data, ct),
            () => client.Close(),
            scope.ServiceProvider);

        _connections[id] = connection;

        // 日志装饰：连接生命周期与帧大小可观测（业务处理器只写业务）
        var handler = new LoggingConnectionHandler(
            _handler,
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LoggingConnectionHandler>());

        Exception? failure = null;
        try
        {
            await handler.OnConnectedAsync(connection, stoppingToken).ConfigureAwait(false);
            await PumpAsync(client, connection, handler, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
        }
        finally
        {
            _connections.TryRemove(id, out _);
            client.Dispose();

            await handler.OnDisconnectedAsync(connection, failure, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(
        TcpClient client,
        ConnectionContext connection,
        IConnectionHandler handler,
        CancellationToken stoppingToken)
    {
        var buffer = new byte[_options.BufferSize];
        var pending = new byte[_options.BufferSize * 4];
        var pendingLength = 0;

        await using var stream = client.GetStream();

        while (!stoppingToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, stoppingToken).ConfigureAwait(false);
            if (read == 0)
                return;   // 对端关闭

            if (pendingLength + read > pending.Length)
                throw new ProtocolException("帧缓冲区溢出：对端可能持续发送不完整的超长帧");

            buffer.AsSpan(0, read).CopyTo(pending.AsSpan(pendingLength));
            pendingLength += read;

            // 切帧：先把本批完整帧收齐（同步、无 IO），再按序异步分发
            var frames = new List<byte[]>();
            pendingLength = _codec.TryReadFrames(pending, pendingLength, frame => frames.Add(frame.ToArray()));

            foreach (var frame in frames)
                await handler.OnFrameAsync(connection, frame, stoppingToken).ConfigureAwait(false);
        }
    }

    private static async Task SendAsync(TcpClient client, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
