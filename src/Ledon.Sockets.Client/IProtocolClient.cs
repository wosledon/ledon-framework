using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Ledon.Protocols;
using Microsoft.Extensions.Logging;

namespace Ledon.Sockets.Client;

/// <summary>客户端选项。</summary>
public sealed class ProtocolClientOptions
{
    /// <summary>服务器地址。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>服务器端口。</summary>
    public int Port { get; set; }

    /// <summary>连接失败重试间隔，默认 2 秒。</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>是否自动重连，默认 <c>true</c>（断线后按 <see cref="ReconnectDelay"/> 重试）。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>单次读取缓冲区，默认 8KB。</summary>
    public int BufferSize { get; set; } = 8192;

    /// <summary>半包拼接上限，默认 1MB。</summary>
    public int MaxPendingBytes { get; set; } = 1024 * 1024;

    /// <summary>发送超时，默认 10 秒。</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>客户端事件。</summary>
public interface IClientHandler
{
    /// <summary>连接建立（可用于登录握手）。</summary>
    Task OnConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>连接断开（<paramref name="exception"/> 非空表示异常断开）。</summary>
    Task OnDisconnectedAsync(Exception? exception, CancellationToken cancellationToken = default);

    /// <summary>收到一个完整帧。</summary>
    Task OnFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);
}

/// <summary>客户端处理器基类。</summary>
public abstract class ClientHandlerBase : IClientHandler
{
    public virtual Task OnConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnDisconnectedAsync(Exception? exception, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// 协议客户端：连接、断线重连、按 <see cref="IFrameCodec"/> 切帧、类型化收发。
/// TCP 与 UDP 两个实现，业务只依赖 <see cref="IProtocolClient"/>。
/// </summary>
public interface IProtocolClient : IAsyncDisposable
{
    /// <summary>是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>连接（已连接时幂位返回）。</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>断开连接。</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>发送原始字节。</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>发送一个协议包（经 <see cref="IPacketCodec{TPacket}"/> 编码）。</summary>
    Task SendPacketAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default);
}
