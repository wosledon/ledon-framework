using Ledon.Protocols;
using Microsoft.Extensions.Logging;

namespace Ledon.Sockets.Server;

/// <summary>连接事件（连上/收到帧/断开）。</summary>
public interface IConnectionHandler
{
    /// <summary>新连接建立。</summary>
    Task OnConnectedAsync(ConnectionContext connection, CancellationToken cancellationToken = default);

    /// <summary>收到一个完整帧（已按 <see cref="IFrameCodec"/> 切好）。</summary>
    Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>连接断开（客户端主动断或异常都在这里，资源清理点）。</summary>
    Task OnDisconnectedAsync(ConnectionContext connection, Exception? exception, CancellationToken cancellationToken = default);
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
