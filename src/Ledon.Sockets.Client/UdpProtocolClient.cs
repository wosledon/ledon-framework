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
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pump;

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

        _client = new UdpClient();
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

    /// <inheritdoc />
    public bool IsConnected => _pump is not null && !_pump.IsCompleted;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_pump is not null)
            return;

        // UDP 无需握手：绑定本地端口并启动接收循环
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        _logger.LogInformation("UDP 客户端已就绪（目标 {Host}:{Port}）", _options.Host, _options.Port);

        await _handler.OnConnectedAsync(cancellationToken).ConfigureAwait(false);

        _pump = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        _lifetime.Cancel();
        _client.Close();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        _client.Send(data.ToArray(), data.Length, new IPEndPoint(IPAddress.Parse(_options.Host), _options.Port));

        return Task.CompletedTask;
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
        _lifetime.Dispose();
        _client.Dispose();
    }
}
