using Microsoft.Extensions.Logging;

namespace Ledon.Protocols;

/// <summary>连接事件（连上/断开/收到帧）。</summary>
public interface IConnectionHandler
{
    /// <summary>新连接建立。</summary>
    Task OnConnectedAsync(ConnectionContext connection, CancellationToken cancellationToken = default);

    /// <summary>收到一个完整帧（已按 <see cref="IFrameCodec"/> 切好）。</summary>
    Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>连接断开（客户端主动断或异常都在这里，资源清理点）。</summary>
    Task OnDisconnectedAsync(ConnectionContext connection, Exception? exception, CancellationToken cancellationToken = default);
}

/// <summary>连接上下文：发送、远端地址、附加状态。</summary>
public sealed class ConnectionContext
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _send;

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

    private readonly Action _onClose;

    /// <summary>连接标识（服务端分配，日志/排查用）。</summary>
    public string Id { get; }

    /// <summary>远端地址（IP:Port）。</summary>
    public string RemoteEndPoint { get; }

    /// <summary>请求作用域服务（按连接解析，如 DbContext/仓储）。</summary>
    public IServiceProvider Services { get; }

    /// <summary>附加状态（业务自用，如登录态、缓冲区）。</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    /// <summary>发送原始字节（已按框架的帧编码规则组帧）。</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _send(data, cancellationToken);

    /// <summary>主动关闭连接。</summary>
    public void Close() => _onClose();
}

/// <summary>处理器基类：只重写关心的事件。</summary>
public abstract class ConnectionHandlerBase : IConnectionHandler
{
    /// <inheritdoc />
    public virtual Task OnConnectedAsync(ConnectionContext connection, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnDisconnectedAsync(ConnectionContext connection, Exception? exception, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>日志适配：把连接事件写进日志（排查"谁连上来又断了"）。</summary>
public sealed class LoggingConnectionHandler(
    IConnectionHandler inner,
    ILogger<LoggingConnectionHandler> logger) : IConnectionHandler
{
    public async Task OnConnectedAsync(ConnectionContext connection, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("连接 {Id} 已建立（{Remote}）", connection.Id, connection.RemoteEndPoint);
        await inner.OnConnectedAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("连接 {Id} 收到 {Length} 字节", connection.Id, frame.Length);
        await inner.OnFrameAsync(connection, frame, cancellationToken).ConfigureAwait(false);
    }

    public async Task OnDisconnectedAsync(ConnectionContext connection, Exception? exception, CancellationToken cancellationToken = default)
    {
        if (exception is null)
            logger.LogInformation("连接 {Id} 已断开", connection.Id);
        else
            logger.LogWarning(exception, "连接 {Id} 异常断开", connection.Id);

        await inner.OnDisconnectedAsync(connection, exception, cancellationToken).ConfigureAwait(false);
    }
}
