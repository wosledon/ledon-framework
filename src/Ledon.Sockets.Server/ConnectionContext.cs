using Ledon.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Sockets.Server;

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
        IServiceProvider services)
    {
        Id = id;
        RemoteEndPoint = remoteEndPoint;
        _send = send;
        _onClose = onClose;
        Services = services;
    }

    /// <summary>连接标识（服务端分配，日志/排查用）。</summary>
    public string Id { get; }

    /// <summary>远端地址（IP:Port）。</summary>
    public string RemoteEndPoint { get; }

    /// <summary>请求作用域服务（按连接解析，如 DbContext/仓储）。</summary>
    public IServiceProvider Services { get; }

    /// <summary>附加状态（业务自用，如登录态、缓冲区、最后心跳）。</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

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
