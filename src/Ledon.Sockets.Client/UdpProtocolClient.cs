using Ledon.Protocols;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Sockets.Client;

/// <summary>
/// UDP 协议客户端：无连接，每次发送都是一个数据报；接收按数据报分发。
/// 适合请求/响应式短协议（DNS 风格）、探测、广播。
/// </summary>
public sealed class UdpProtocolClient : IProtocolClient
{
    private readonly ProtocolClientOptions _options;
    private readonly IPacketCodec<object>? _codec;
    private readonly IClientHandler _handler;
    private readonly ILogger<UdpProtocolClient> _logger;
    private readonly UdpClient _client;
    private readonly IPEndPoint _remote;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private Task? _pump;
    private int _connected;

    public UdpProtocolClient(
        ProtocolClientOptions options,
        IClientHandler handler,
        IPacketCodec<object> codec,
        ILogger<UdpProtocolClient>? logger = null)
    {
        _options = options;
        _codec = codec;
        _handler = handler;
        _logger = logger ?? NullLogger<UdpProtocolClient>.Instance;

        _remote = new IPEndPoint(IPAddress.Parse(options.Host), options.Port);
        _client = new UdpClient(AddressFamily.InterNetwork);
    }

    /// <summary>
    /// 数据报即消息的客户端（SIP/DNS 风格）：不需要包编解码，
    /// 直接用 <see cref="SendAsync"/> 发原始字节。
    /// </summary>
    public UdpProtocolClient(
        ProtocolClientOptions options,
        IClientHandler handler,
        ILogger<UdpProtocolClient>? logger = null)
        : this(options, handler, codec: null!, logger)
    {
    }

    /// <summary>本地绑定端口（<see cref="ProtocolClientOptions.LocalPort"/> 配 0 时由系统分配）。</summary>
    public int LocalPort => ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        // SIP 这类协议要把本地端口写进 Via/Contact：必须支持固定端口，不能用随机 ephemeral
        _client.Client.Bind(new IPEndPoint(
            _options.LocalBindAddress is null ? IPAddress.Any : IPAddress.Parse(_options.LocalBindAddress),
            _options.LocalPort));

        _logger.LogInformation("UDP 客户端已就绪（本地 {Local}，目标 {Remote}）", LocalPort, _remote);

        Volatile.Write(ref _connected, 1);

        await _handler.OnConnectedAsync(cancellationToken).ConfigureAwait(false);

        _pump ??= Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _connected, 0);

        await _lifetime.CancelAsync().ConfigureAwait(false);

        var pump = _pump;
        _pump = null;

        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("接收循环退出：{Message}", ex.Message);
            }
        }

        _client.Close();
    }

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        // UdpClient 的同步 Send 内部有锁，但显式串行更直观，也避免和接收侧相互抢锁
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _client.SendAsync(data, _remote, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public Task SendPacketAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
    {
        // 包编解码是可选的：数据报即消息的协议（SIP/DNS 风格）不需要它
        var codec = _codec as IPacketCodec<TPacket>
            ?? throw new InvalidOperationException(
                $"未注册 IPacketCodec<{typeof(TPacket).Name}>（数据报即消息的协议请用 SendAsync 发原始字节）");

        return SendAsync(codec.Encode(packet), cancellationToken);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                await _handler.OnFrameAsync(result.Buffer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP 接收失败");
            await _handler.OnDisconnectedAsync(ex, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);

        _sendGate.Dispose();
        _lifetime.Dispose();
        _client.Dispose();
    }
}
