using Ledon.Protocols;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Ledon.Sockets.Client;

/// <summary>
/// TCP 协议客户端：连接 + 断线自动重连 + 帧切分 + 类型化发送。
/// 单实例使用（一个客户端一个连接）；多连接场景创建多个实例。
/// </summary>
public sealed class TcpProtocolClient : IProtocolClient
{
    private readonly ProtocolClientOptions _options;
    private readonly IFrameCodec _codec;
    private readonly IClientHandler _handler;
    private readonly ILogger<TcpProtocolClient> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private TcpClient? _client;
    private Task? _pump;
    private CancellationTokenSource? _lifetime;

    public TcpProtocolClient(
        ProtocolClientOptions options,
        IFrameCodec codec,
        IClientHandler handler,
        ILogger<TcpProtocolClient> logger)
    {
        _options = options;
        _codec = codec;
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConnected => _client?.Connected == true;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return;

            _lifetime?.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var client = new TcpClient();
            await client.ConnectAsync(_options.Host, _options.Port, _lifetime.Token).ConfigureAwait(false);
            _client = client;

            _logger.LogInformation("已连接 {Host}:{Port}", _options.Host, _options.Port);

            await _handler.OnConnectedAsync(_lifetime.Token).ConfigureAwait(false);

            _pump = Task.Run(() => PumpAsync(client, _lifetime.Token), CancellationToken.None);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        _client = null;

        if (client is not null)
        {
            client.Close();
            client.Dispose();
        }

        if (_lifetime is not null)
            await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("读取循环退出：{Message}", ex.Message);
            }

            _pump = null;
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("尚未连接：请先调用 ConnectAsync");

        using var timeout = new CancellationTokenSource(_options.SendTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var stream = client.GetStream();
        await stream.WriteAsync(data, linked.Token).ConfigureAwait(false);
        await stream.FlushAsync(linked.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendPacketAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
    {
        var codec = ResolveCodec<TPacket>();

        return SendAsync(codec.Encode(packet), cancellationToken);
    }

    private IPacketCodec<TPacket> ResolveCodec<TPacket>() =>
        _codec as IPacketCodec<TPacket>
        ?? throw new InvalidOperationException($"客户端编解码器不匹配：需要 IPacketCodec<{typeof(TPacket).Name}>");

    private async Task PumpAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.BufferSize];
        var pending = new byte[_options.MaxPendingBytes];
        var pendingLength = 0;

        try
        {
            await using var stream = client.GetStream();

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("对端关闭了连接");

                if (pendingLength + read > pending.Length)
                    throw new ProtocolException($"帧缓冲超过上限 {pending.Length} 字节");

                buffer.AsSpan(0, read).CopyTo(pending.AsSpan(pendingLength));
                pendingLength += read;

                var frames = new List<byte[]>();
                pendingLength = _codec.TryReadFrames(pending, pendingLength, frame => frames.Add(frame.ToArray()));

                foreach (var frame in frames)
                    await _handler.OnFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭：正常路径
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "连接异常断开");

            await _handler.OnDisconnectedAsync(ex, CancellationToken.None).ConfigureAwait(false);
        }

        _client = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        _lifetime?.Dispose();
    }
}
