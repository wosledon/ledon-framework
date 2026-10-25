using System.Net;
using Ledon.Protocols;
using Ledon.Sip;
using Ledon.Sockets.Server;
using Microsoft.Extensions.Logging;

namespace Ledon.Sip.Server;

/// <summary>
/// SIP over UDP 处理器：把 <see cref="Ledon.Sockets.Server.UdpServer"/> 收到的数据报
/// 解析成 <see cref="SipMessage"/>，经 <see cref="SipTransactionLayer"/> 交给
/// <see cref="ISipHandler"/>，并把响应发回来源。
/// <para>
/// 设计：协议语义（解析/应答/事务）在 <c>Ledon.Sip</c>，传输（socket/作用域/并发）
/// 在 <c>Ledon.Sockets.Server</c>——本类只是两者之间的适配器。
/// </para>
/// </summary>
public sealed class SipUdpHandler : IUdpHandler
{
    private readonly ISipHandler _handler;
    private readonly SipTransactionLayer _transactions;
    private readonly ILogger<SipUdpHandler> _logger;

    public SipUdpHandler(
        ISipHandler handler,
        SipTransactionLayer transactions,
        ILogger<SipUdpHandler> logger)
    {
        _handler = handler;
        _transactions = transactions;
        _logger = logger;
    }

    public async Task OnDatagramAsync(UdpContext datagram, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = SipParser.Parse(datagram.Data.Span);

            _logger.LogDebug("SIP {Type} {Method}{StatusCode} 来自 {Remote}",
                request.Type, request.Method ?? "", request.StatusCode, datagram.Remote);

            // 当前数据报的来源就是响应的去处；事务层的重传也走同一个通道
            await _transactions.HandleAsync(
                request,
                (payload, ct) => datagram.ReplyAsync(payload, ct),
                (message, ct) => _handler.HandleAsync(message, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 解析失败只记录：UDP 无连接，不能"断开"任何人
            _logger.LogWarning(ex, "SIP 报文处理失败（{Remote}）", datagram.Remote);
        }
    }
}

/// <summary>
/// SIP over TCP 处理器：按 <see cref="SipTcpFrameCodec"/> 切帧后走同一个事务层。
/// TCP 是可靠传输，事务层不会重传响应。
/// </summary>
public sealed class SipTcpHandler : IConnectionHandler
{
    private readonly ISipHandler _handler;
    private readonly SipTransactionLayer _transactions;
    private readonly ILogger<SipTcpHandler> _logger;

    public SipTcpHandler(
        ISipHandler handler,
        SipTransactionLayer transactions,
        ILogger<SipTcpHandler> logger)
    {
        _handler = handler;
        _transactions = transactions;
        _logger = logger;
    }

    public Task OnConnectedAsync(ConnectionContext connection, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task OnFrameAsync(
        ConnectionContext connection,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = SipParser.Parse(frame.Span);

            _logger.LogDebug("SIP/TCP {Method} 来自 {Remote}", request.Method ?? "", connection.RemoteEndPoint);

            // 事务层经 Sender 把响应写回这条连接（TCP 可靠，不需要端点）
            await _transactions.HandleAsync(
                request,
                (payload, ct) => connection.SendAsync(payload, ct),
                (message, ct) => _handler.HandleAsync(message, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP/TCP 报文处理失败（{Remote}）", connection.RemoteEndPoint);
        }
    }

    public Task OnDisconnectedAsync(
        ConnectionContext connection,
        Exception? exception,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
