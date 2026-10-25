using Ledon.Protocols;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Sockets.Server;

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

    /// <summary>半包拼接缓冲区上限，默认 1MB（超出视为对端异常，断开）。</summary>
    public int MaxPendingBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// 单次发送超时，默认 30 秒。对端只连不发（或不再读）时，写操作会一直挂着；
    /// 超时即断开，避免一个恶意连接拖住处理器任务和文件描述符。
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 空闲超时，默认 5 分钟：多久没收到的字节就断开。设为 <see cref="TimeSpan.Zero"/> 关闭
    /// （只建议内网可信连接这么做）。
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>最大并发连接数，默认 1000；超出直接拒绝新连接（不让文件描述符被耗尽）。</summary>
    public int MaxConnections { get; set; } = 1000;

    /// <summary>TCP keepalive 间隔，默认 30 秒（配合 <see cref="IdleTimeout"/> 双保险）。</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否关闭 Nagle 算法（小帧低延迟），默认 <c>true</c>。</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>停止时等待在途处理完成的最长时间，默认 5 秒。</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// TCP 服务端：监听 → 接受连接 → 按 <see cref="IFrameCodec"/> 切帧 → 分发给
/// <see cref="IConnectionHandler"/>。
/// <list type="bullet">
///   <item>每连接一个独立读取循环 + 独立 DI 作用域，互不阻塞；</item>
///   <item>帧按到达顺序处理；单帧处理失败只记录不断开；</item>
///   <item><see cref="ProtocolException"/>（协议不同步）断开该连接，不影响其他连接；</item>
///   <item>半包自动拼接；缓冲溢出保护（对端持续发不完整帧时断开而不是 OOM）；</item>
///   <item>发送串行 + 发送超时：同一连接的并发写不会交叉，慢读者不会挂住处理器；</item>
///   <item>空闲超时 + 连接数上限：静默连接和洪水连接都不会耗尽资源；</item>
///   <item>接受循环容错：单次 accept 失败只记录，不会让监听器停摆。</item>
/// </list>
/// </summary>
public sealed class TcpServer : BackgroundService
{
    private readonly TcpServerOptions _options;
    private readonly IFrameCodec _codec;
    private readonly IConnectionHandler _handler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TcpServer> _logger;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, ConnectionContext> _connections = new(StringComparer.Ordinal);

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

    /// <summary>累计拒绝的连接数（超过 <see cref="TcpServerOptions.MaxConnections"/>）。</summary>
    public long RejectedConnections { get; private set; }

    /// <summary>当前全部连接（运维/广播用）。</summary>
    public IReadOnlyCollection<ConnectionContext> Connections => _connections.Values.ToArray();

    /// <summary>向全部连接广播原始字节（慢连接互不阻塞，逐个隔离失败）。</summary>
    public Task BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var payload = data.ToArray();   // 快照：避免调用方在广播途中改缓冲

        return Task.WhenAll(_connections.Values.Select(connection => Task.Run(async () =>
        {
            try
            {
                await connection.SendAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "广播到连接 {Id} 失败", connection.Id);
            }
        }, CancellationToken.None)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start(_options.Backlog);
        _logger.LogInformation("TCP 服务端已监听 {Address}:{Port}", _options.Address, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;   // 宿主持停
                }
                catch (Exception ex)
                {
                    // accept 失败（句柄耗尽/瞬时错误）不能让监听器停摆：
                    // 否则服务端会在没有任何日志提示的情况下永久停止接受
                    _logger.LogError(ex, "接受连接失败（继续监听）");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);

                    continue;
                }

                if (_connections.Count >= _options.MaxConnections)
                {
                    RejectedConnections++;
                    _logger.LogWarning("连接数已达上限 {Max}，拒绝 {Remote}",
                        _options.MaxConnections, client.Client.RemoteEndPoint);

                    client.Dispose();

                    continue;
                }

                // 每连接一个任务：一个慢连接不会拖垮其他连接
                _ = Task.Run(() => ServeAsync(client, stoppingToken), CancellationToken.None);
            }
        }
        finally
        {
            _listener.Stop();
        }

        // 优雅退出：给在途处理一个窗口，再强制关闭
        await DrainAsync().ConfigureAwait(false);
    }

    /// <summary>停止时等待在途请求完成（有上限，避免无限拖住宿主持停）。</summary>
    private async Task DrainAsync()
    {
        if (_connections.IsEmpty)
            return;

        _logger.LogInformation("TCP 服务端正在等待 {Count} 条连接结束在途处理", _connections.Count);

        var deadline = DateTime.UtcNow + _options.ShutdownTimeout;

        while (!_connections.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);

        foreach (var connection in _connections.Values)
            connection.Close();

        _connections.Clear();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        foreach (var connection in _connections.Values)
            connection.Close();

        _connections.Clear();
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var id = $"tcp-{Interlocked.Increment(ref _connectionSeq)}";
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        // 连接级生命周期：空闲超时/发送超时/协议错误只取消这一条连接
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();

        var writeGate = new SemaphoreSlim(1, 1);
        var connection = new ConnectionContext(
            id,
            remote,
            (data, ct) => SendAsync(client, writeGate, lifetime, data, ct),
            () => lifetime.Cancel(),
            scope.ServiceProvider,
            writeGate,
            lifetime.Token);

        _connections[id] = connection;
        TcpTuning.Apply(client, _options.NoDelay, _options.KeepAliveInterval);

        // 日志装饰：连接生命周期可观测（业务处理器只写业务）
        var handler = new LoggingConnectionHandler(
            _handler,
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LoggingConnectionHandler>());

        Exception? failure = null;
        try
        {
            await handler.OnConnectedAsync(connection, stoppingToken).ConfigureAwait(false);
            await PumpAsync(client, connection, handler, stoppingToken, lifetime).ConfigureAwait(false);
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
        CancellationToken stoppingToken,
        CancellationTokenSource lifetime)
    {
        var buffer = new byte[_options.BufferSize];
        var pending = new byte[_options.MaxPendingBytes];
        var pendingLength = 0;

        await using var stream = client.GetStream();

        while (!lifetime.IsCancellationRequested)
        {
            // 空闲超时：每次读前重新武装；超时会取消 lifetime，读取随即退出
            using var idle = _options.IdleTimeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)
                : null;

            idle?.CancelAfter(_options.IdleTimeout);

            int read;
            try
            {
                read = await stream.ReadAsync(buffer, (idle?.Token ?? lifetime.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                throw new TimeoutException($"连接空闲超过 {_options.IdleTimeout.TotalSeconds} 秒");
            }

            if (read == 0)
                return;   // 对端关闭

            connection.Stats.AddReceived(read);

            if (pendingLength + read > pending.Length)
                throw new ProtocolException($"帧缓冲超过上限 {pending.Length} 字节：对端可能持续发送不完整帧");

            buffer.AsSpan(0, read).CopyTo(pending.AsSpan(pendingLength));
            pendingLength += read;

            // 切帧：先把本批完整帧收齐（同步、无 IO），再按序异步分发
            var frames = new List<byte[]>();
            pendingLength = _codec.TryReadFrames(pending, pendingLength, frame =>
            {
                frames.Add(frame.ToArray());
                connection.Stats.AddFrame();
            });

            foreach (var frame in frames)
                await handler.OnFrameAsync(connection, frame, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 发送：写门闩保证同一连接的字节不交叉，发送超时保证慢读者不会挂住调用方。
    /// </summary>
    private async Task SendAsync(
        TcpClient client,
        SemaphoreSlim writeGate,
        CancellationTokenSource lifetime,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(_options.SendTimeout);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var stream = client.GetStream();
            await stream.WriteAsync(data, linked.Token).ConfigureAwait(false);
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
