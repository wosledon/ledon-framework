using Ledon.Protocols;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Sockets.Server;

/// <summary>UDP 服务端选项。</summary>
public sealed class UdpServerOptions
{
    /// <summary>绑定地址，默认 <c>0.0.0.0</c>。</summary>
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>绑定端口。</summary>
    public int Port { get; set; }

    /// <summary>
    /// 并发处理上限，默认 32。UDP 没有连接概念，洪水数据报会无限派发任务；
    /// 超出并发的数据报进队列等待，队列也满时才丢（并计数）。
    /// </summary>
    public int MaxConcurrency { get; set; } = 32;

    /// <summary>等待处理的队列容量，默认 1000；满时丢最旧的并计数。</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>接收缓冲区（字节），默认 64KB。</summary>
    public int ReceiveBufferSize { get; set; } = 64 * 1024;
}

/// <summary>UDP 数据报上下文（无连接，每次都是独立数据报；可回包）。</summary>
public sealed class UdpContext
{
    private readonly UdpServer _server;

    internal UdpContext(ReadOnlyMemory<byte> data, IPEndPoint remote, IServiceProvider services, UdpServer server)
    {
        Data = data;
        Remote = remote;
        Services = services;
        _server = server;
    }

    /// <summary>数据报内容。</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>来源端点。</summary>
    public IPEndPoint Remote { get; }

    /// <summary>请求作用域服务。</summary>
    public IServiceProvider Services { get; }

    /// <summary>回包给来源（请求/响应式协议用）。</summary>
    public Task ReplyAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _server.SendAsync(data, Remote, cancellationToken);

    /// <summary>按 <see cref="IPacketCodec{TPacket}"/> 编码后回包。</summary>
    public Task ReplyPacketAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
    {
        var codec = Services.GetService<IPacketCodec<TPacket>>()
            ?? throw new InvalidOperationException($"未注册 IPacketCodec<{typeof(TPacket).Name}>");

        return ReplyAsync(codec.Encode(packet), cancellationToken);
    }
}

/// <summary>UDP 数据报处理器。</summary>
public interface IUdpHandler
{
    /// <summary>收到一个数据报（并发调用，实现须线程安全）。</summary>
    Task OnDatagramAsync(UdpContext datagram, CancellationToken cancellationToken = default);
}

/// <summary>UDP 累计统计（运维用）。</summary>
public sealed class UdpServerStats
{
    private long _received;
    private long _dispatched;
    private long _dropped;
    private long _failed;

    /// <summary>累计收到的数据报数。</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>累计交到处理器的数据报数。</summary>
    public long Dispatched => Interlocked.Read(ref _dispatched);

    /// <summary>累计丢弃的数据报数（队列满）。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>累计处理失败的数据报数。</summary>
    public long Failed => Interlocked.Read(ref _failed);

    internal void AddReceived() => Interlocked.Increment(ref _received);

    internal void AddDispatched() => Interlocked.Increment(ref _dispatched);

    internal void AddDropped() => Interlocked.Increment(ref _dropped);

    internal void AddFailed() => Interlocked.Increment(ref _failed);
}

/// <summary>
/// UDP 服务端：接收数据报并分发给 <see cref="IUdpHandler"/>。
/// UDP 无连接、无顺序保证，处理器天然并发调用——实现须线程安全。
/// <para>
/// 派发是**有界并发**的：洪水数据报不会每个都起一个任务（那会打爆线程池），
/// 超出并发的进队列，队列满才丢并计数。
/// </para>
/// </summary>
public sealed class UdpServer : BackgroundService
{
    private readonly UdpServerOptions _options;
    private readonly IUdpHandler _handler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UdpServer> _logger;
    private readonly UdpClient _client;
    private readonly ConcurrentQueue<(byte[] Data, IPEndPoint Remote)> _queue = new();
    private readonly SemaphoreSlim _work = new(0, int.MaxValue);

    public UdpServer(
        UdpServerOptions options,
        IUdpHandler handler,
        IServiceScopeFactory scopeFactory,
        ILogger<UdpServer> logger)
    {
        _options = options;
        _handler = handler;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _client = new UdpClient(new IPEndPoint(IPAddress.Parse(options.Address), options.Port));
    }

    /// <summary>当前绑定端口（端口配 0 时由系统分配后从这里读）。</summary>
    public int BoundPort => ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

    /// <summary>累计统计。</summary>
    public UdpServerStats Stats { get; } = new();

    /// <summary>主动发送数据报（应答/探测用）。</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken cancellationToken = default) =>
        _client.SendAsync(data, remote, cancellationToken).AsTask();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UDP 服务端已绑定 {Address}:{Port}（并发上限 {Max}）",
            _options.Address, BoundPort, _options.MaxConcurrency);

        var workers = Enumerable.Range(0, _options.MaxConcurrency)
            .Select(_ => Task.Run(() => DrainAsync(stoppingToken), CancellationToken.None))
            .ToArray();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult result;

                try
                {
                    result = await _client.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UDP 接收失败（继续接收）");
                    continue;
                }

                Stats.AddReceived();

                if (_queue.Count >= _options.QueueCapacity)
                {
                    Stats.AddDropped();
                    _logger.LogWarning("UDP 队列已满（{Capacity}），丢弃来自 {Remote} 的数据报",
                        _options.QueueCapacity, result.RemoteEndPoint);

                    continue;
                }

                _queue.Enqueue((result.Buffer, result.RemoteEndPoint));
                _work.Release();
            }
        }
        finally
        {
            // 工作线程的 WaitAsync 会被 stoppingToken 取消后自行退出
            await Task.WhenAll(workers).ConfigureAwait(false);
            _client.Dispose();
        }
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _work.WaitAsync(stoppingToken).ConfigureAwait(false);

                if (!_queue.TryDequeue(out var item))
                    continue;

                await using var scope = _scopeFactory.CreateAsyncScope();

                try
                {
                    Stats.AddDispatched();

                    await _handler.OnDatagramAsync(
                        new UdpContext(item.Data, item.Remote, scope.ServiceProvider, this),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Stats.AddFailed();
                    _logger.LogError(ex, "UDP 数据报处理失败（{Remote}）", item.Remote);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主持停：正常退出
        }
    }
}
