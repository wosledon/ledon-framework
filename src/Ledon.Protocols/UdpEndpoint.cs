using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Protocols;

/// <summary>UDP 端点选项。</summary>
public sealed class UdpEndpointOptions
{
    /// <summary>绑定地址，默认 <c>0.0.0.0</c>。</summary>
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>绑定端口。</summary>
    public int Port { get; set; }

    /// <summary>接收缓冲区，默认 64KB（UDP 数据报上限）。</summary>
    public int BufferSize { get; set; } = 65536;
}

/// <summary>UDP 数据报上下文（无连接，每次都是独立数据报）。</summary>
public sealed class UdpContext
{
    internal UdpContext(ReadOnlyMemory<byte> data, IPEndPoint remote, IServiceProvider services)
    {
        Data = data;
        Remote = remote;
        Services = services;
    }

    /// <summary>数据报内容。</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>来源端点。</summary>
    public IPEndPoint Remote { get; }

    /// <summary>请求作用域服务。</summary>
    public IServiceProvider Services { get; }
}

/// <summary>UDP 数据报处理器。</summary>
public interface IUdpHandler
{
    /// <summary>收到一个数据报（并发调用，实现须线程安全）。</summary>
    Task OnDatagramAsync(UdpContext datagram, CancellationToken cancellationToken = default);
}

/// <summary>
/// UDP 端点：接收数据报并分发给 <see cref="IUdpHandler"/>。
/// UDP 无连接、无顺序保证，处理器天然并发调用——实现须线程安全。
/// </summary>
public sealed class UdpEndpoint : BackgroundService
{
    private readonly UdpEndpointOptions _options;
    private readonly IUdpHandler _handler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UdpEndpoint> _logger;
    private readonly UdpClient _client;

    public UdpEndpoint(
        UdpEndpointOptions options,
        IUdpHandler handler,
        IServiceScopeFactory scopeFactory,
        ILogger<UdpEndpoint> logger)
    {
        _options = options;
        _handler = handler;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _client = new UdpClient(new IPEndPoint(IPAddress.Parse(options.Address), options.Port));
    }

    /// <summary>当前绑定端口（端口配 0 时由系统分配后从这里读）。</summary>
    public int BoundPort => ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

    /// <summary>主动发送数据报（应答/探测用）。</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        // UDP 发送不进内核阻塞路径：同步 Send 即可（语义与异步等价）
        _client.Send(data.ToArray(), data.Length, remote);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UDP 端点已绑定 {Address}:{Port}", _options.Address, BoundPort);

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

            // 每数据报一个作用域 + 一个任务：并发处理，互不阻塞
            _ = Task.Run(async () =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                try
                {
                    await _handler.OnDatagramAsync(
                        new UdpContext(result.Buffer, result.RemoteEndPoint, scope.ServiceProvider),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UDP 数据报处理失败（{Remote}）", result.RemoteEndPoint);
                }
            }, CancellationToken.None);
        }

        _client.Dispose();
    }
}
