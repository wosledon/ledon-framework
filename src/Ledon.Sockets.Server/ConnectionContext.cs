using System.Collections.Concurrent;
using System.Net.Sockets;
using Ledon.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
namespace Ledon.Sockets.Server;

/// <summary>连接的累计统计（运维/观测用；读写都是原子的）。</summary>
public sealed class ConnectionStats
{
    private long _received;
    private long _sent;
    private long _frames;

    /// <summary>连接建立时间（UTC）。</summary>
    public DateTimeOffset ConnectedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>累计收到的字节数（含半包拼接部分）。</summary>
    public long ReceivedBytes => Interlocked.Read(ref _received);

    /// <summary>累计发出的字节数。</summary>
    public long SentBytes => Interlocked.Read(ref _sent);

    /// <summary>累计切出的完整帧数。</summary>
    public long Frames => Interlocked.Read(ref _frames);

    internal void AddReceived(long bytes) => Interlocked.Add(ref _received, bytes);

    internal void AddSent(long bytes) => Interlocked.Add(ref _sent, bytes);

    internal void AddFrame() => Interlocked.Increment(ref _frames);
}

/// <summary>连接上下文：发送、远端地址、附加状态。</summary>
public sealed class ConnectionContext
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _send;
    private readonly Action _onClose;

    internal ConnectionContext(
        string id,
        string remoteEndPoint,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> send,
        Action onClose,
        IServiceProvider services,
        SemaphoreSlim writeGate,
        CancellationToken lifetime)
    {
        Id = id;
        RemoteEndPoint = remoteEndPoint;
        _send = send;
        _onClose = onClose;
        Services = services;
        WriteGate = writeGate;
        Lifetime = lifetime;
    }

    /// <summary>连接标识（服务端分配，日志/排查用）。</summary>
    public string Id { get; }

    /// <summary>远端地址（IP:Port）。</summary>
    public string RemoteEndPoint { get; }

    /// <summary>请求作用域服务（按连接解析，如 DbContext/仓储）。</summary>
    public IServiceProvider Services { get; }

    /// <summary>附加状态（业务自用，如登录态、缓冲区、最后心跳）。并发安全。</summary>
    public ConcurrentDictionary<object, object?> Items { get; } = new();

    /// <summary>累计统计（字节数/帧数/建立时间）。</summary>
    public ConnectionStats Stats { get; } = new();

    /// <summary>写门闩：同一连接的发送必须串行（并发写会把帧交叉成乱码）。</summary>
    internal SemaphoreSlim WriteGate { get; }

    /// <summary>连接生命周期令牌：连接关闭/超时/协议错误时取消。</summary>
    internal CancellationToken Lifetime { get; }

    /// <summary>发送原始字节（已按框架的帧编码规则组帧）。</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _send(data, cancellationToken);

    /// <summary>发送一个协议包（经 <see cref="IPacketCodec{TPacket}"/> 编码 + 帧编解码组帧）。</summary>
    public Task SendPacketAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
    {
        var codec = Services.GetService<IPacketCodec<TPacket>>()
            ?? throw new InvalidOperationException($"未注册 IPacketCodec<{typeof(TPacket).Name}>");

        var frame = codec.Encode(packet);

        return _send(frame, cancellationToken);
    }

    /// <summary>主动关闭连接。</summary>
    public void Close() => _onClose();
}
