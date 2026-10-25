using Ledon.Protocols;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Ledon.Sockets.Client;

/// <summary>
/// TCP 协议客户端：连接 + 断线自动重连 + 帧切分 + 类型化发送。
/// 单实例使用（一个客户端一个连接）；多连接场景创建多个实例。
/// <para>
/// 连接生命周期由单一的"监管循环"持有：<see cref="ConnectAsync"/> 只是请求并等待它连上，
/// 因此手动重连与自动重连不会各建一条连接（否则旧连接没人读，响应全丢）。
/// </para>
/// </summary>
public sealed class TcpProtocolClient : IProtocolClient
{
    private readonly ProtocolClientOptions _options;
    private readonly IFrameCodec _codec;
    private readonly IClientHandler _handler;
    private readonly ILogger<TcpProtocolClient> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>"现在就要连接"信号：打断退避等待，让主动 ConnectAsync 不必干等。</summary>
    private readonly SemaphoreSlim _connectRequested = new(0, 1);

    private readonly CancellationTokenSource _supervisorCts = new();

    private TcpClient? _client;
    private CancellationTokenSource? _lifetime;
    private Task? _supervisor;

    private int _connected;
    private int _wantConnected;
    private int _closing;
    private int _disposing;

    /// <summary>连接代号：每次成功建立自增。用于识别"连上过"（哪怕立刻又被踢掉）。</summary>
    private int _connectGeneration;
    private Exception? _lastConnectError;

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
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsConnected)
                return;

            Volatile.Write(ref _closing, 0);
            Volatile.Write(ref _wantConnected, 1);
            Volatile.Write(ref _lastConnectError, null);

            if (!_options.AutoReconnect)
            {
                // 一次性模式：失败直接抛给调用方，后台不重试
                await EstablishAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            EnsureSupervisor();
            _connectRequested.Release();

            // 等监管循环连上；本次尝试失败要把异常抛出来（后台仍会继续重试）。
            // 用"连接代号"而不是轮询 IsConnected：被服务端拒绝的连接是连上又立刻踢掉，
            // 轮询会整窗口错过，白白等到超时。
            var generation = Volatile.Read(ref _connectGeneration);
            var deadline = DateTime.UtcNow + _options.ConnectTimeout;

            while (!IsConnected && Volatile.Read(ref _connectGeneration) == generation)
            {
                if (Volatile.Read(ref _closing) != 0)
                    throw new OperationCanceledException("客户端已关闭");

                if (Volatile.Read(ref _lastConnectError) is { } error)
                {
                    Volatile.Write(ref _lastConnectError, null);
                    throw error;
                }

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"连接 {_options.Host}:{_options.Port} 超时（{_options.ConnectTimeout.TotalSeconds} 秒）");

                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>确保监管循环在跑（幂等）。</summary>
    private void EnsureSupervisor()
    {
        if (_supervisor is { IsCompleted: false })
            return;

        _supervisor = Task.Run(SuperviseAsync, CancellationToken.None);
    }

    /// <summary>
    /// 监管循环：唯一的建连者。建立 → 读循环 → 断开通知 → 退避 → 再建立。
    /// </summary>
    private async Task SuperviseAsync()
    {
        var attempt = 0;

        while (Volatile.Read(ref _closing) == 0 && Volatile.Read(ref _disposing) == 0)
        {
            if (Volatile.Read(ref _wantConnected) == 0)
            {
                try
                {
                    await _connectRequested.WaitAsync(_supervisorCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await EstablishAsync(CancellationToken.None).ConfigureAwait(false);

                attempt = 0;
                Volatile.Write(ref _lastConnectError, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Volatile.Write(ref _lastConnectError, ex);
                _logger.LogWarning(ex, "连接 {Host}:{Port} 失败", _options.Host, _options.Port);

                DrainRequests();   // 本次尝试已回应请求：别让退避被绕过变成忙循环
                await DelayAsync(ComputeBackoff(attempt++)).ConfigureAwait(false);

                continue;
            }

            var failure = await ReadLoopAsync().ConfigureAwait(false);

            Volatile.Write(ref _connected, 0);

            if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _wantConnected) == 0)
                return;

            await _handler.OnDisconnectedAsync(failure, CancellationToken.None).ConfigureAwait(false);

            DrainRequests();
            await DelayAsync(ComputeBackoff(attempt++)).ConfigureAwait(false);
        }
    }

    /// <summary>建立一条新连接（连接级令牌 + 发送/读取都挂在它上面）。</summary>
    private async Task EstablishAsync(CancellationToken cancellationToken)
    {
        _lifetime?.Dispose();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var client = new TcpClient();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(_options.ConnectTimeout);

            await client.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        TcpTuning.Apply(client, _options.NoDelay, _options.KeepAliveInterval);

        _client = client;
        Volatile.Write(ref _connected, 1);
        Interlocked.Increment(ref _connectGeneration);

        _logger.LogInformation("已连接 {Host}:{Port}", _options.Host, _options.Port);

        await _handler.OnConnectedAsync(_lifetime.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 读循环：返回 null 表示对端正常关闭，否则返回导致断开的异常。
    /// 只负责"把字节切成帧交给处理器"，重连决策在监管循环里。
    /// </summary>
    private async Task<Exception?> ReadLoopAsync()
    {
        var client = _client;
        var lifetime = _lifetime;

        if (client is null || lifetime is null)
            return null;

        var buffer = new byte[_options.BufferSize];
        var pending = new byte[_options.MaxPendingBytes];
        var pendingLength = 0;

        try
        {
            await using var stream = client.GetStream();

            while (!lifetime.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;   // 主动关闭：正常路径
                }

                if (read == 0)
                    return null;   // 对端关闭

                if (pendingLength + read > pending.Length)
                    return new ProtocolException($"帧缓冲超过上限 {pending.Length} 字节");

                buffer.AsSpan(0, read).CopyTo(pending.AsSpan(pendingLength));
                pendingLength += read;

                var frames = new List<byte[]>();
                pendingLength = _codec.TryReadFrames(pending, pendingLength, frame => frames.Add(frame.ToArray()));

                foreach (var frame in frames)
                    await _handler.OnFrameAsync(frame, lifetime.Token).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "连接异常断开");

            return ex;
        }
    }

    /// <summary>退避等待：期间收到新的连接请求就立刻返回。</summary>
    private async Task DelayAsync(TimeSpan delay)
    {
        try
        {
            await _connectRequested.WaitAsync(delay).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退避到期
        }
    }

    private void DrainRequests()
    {
        while (_connectRequested.Wait(TimeSpan.Zero))
        {
            // 吞掉积压信号
        }
    }

    /// <summary>指数退避（封顶）+ 25% 抖动：避免服务端重启后所有客户端同时重连把它打满。</summary>
    private TimeSpan ComputeBackoff(int attempt)
    {
        var ceiling = _options.MaxReconnectDelay;
        var growth = Math.Min(attempt, 16);

        var ticks = _options.ReconnectDelay.Ticks * (1L << growth);
        var capped = ticks >= ceiling.Ticks ? ceiling : TimeSpan.FromTicks(ticks);

        var jitter = 0.75 + (Random.Shared.NextDouble() * 0.5);   // 0.75 ~ 1.25

        return TimeSpan.FromTicks((long)(capped.Ticks * jitter));
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _closing, 1);
        Volatile.Write(ref _wantConnected, 0);

        await _supervisorCts.CancelAsync().ConfigureAwait(false);

        var client = _client;
        _client = null;
        Volatile.Write(ref _connected, 0);

        if (client is not null)
        {
            client.Close();
            client.Dispose();
        }

        if (_lifetime is not null)
            await _lifetime.CancelAsync().ConfigureAwait(false);

        var supervisor = _supervisor;
        _supervisor = null;

        if (supervisor is not null)
        {
            try
            {
                await supervisor.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("监管循环退出：{Message}", ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var client = Volatile.Read(ref _client)
            ?? throw new InvalidOperationException("尚未连接：请先调用 ConnectAsync");

        // 同一连接的并发写必须串行：NetworkStream 不保证两次 WriteAsync 的字节不交叉
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.SendTimeout);

            var stream = client.GetStream();
            await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public Task SendPacketAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
    {
        var codec = _codec as IPacketCodec<TPacket>
            ?? throw new InvalidOperationException($"客户端编解码器不匹配：需要 IPacketCodec<{typeof(TPacket).Name}>");

        return SendAsync(codec.Encode(packet), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposing, 1);

        await CloseAsync().ConfigureAwait(false);

        _connectGate.Dispose();
        _sendGate.Dispose();
        _connectRequested.Dispose();
        _supervisorCts.Dispose();
        _lifetime?.Dispose();
    }
}
