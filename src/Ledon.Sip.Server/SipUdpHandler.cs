using Ledon.Protocols;
using Ledon.Sockets.Server;
using Microsoft.Extensions.Logging;

namespace Ledon.Sip.Server;

/// <summary>
/// SIP over UDP 处理器：把 <see cref="Ledon.Sockets.Server.UdpServer"/> 收到的数据报
/// 解析成 <see cref="SipMessage"/>，交给 <see cref="ISipHandler"/>，并把响应发回来源。
/// <para>
/// 设计：协议语义（解析/应答）在 <c>Ledon.Sip</c>，传输（socket/作用域/并发）
/// 在 <c>Ledon.Sockets.Server</c>——本类只是两者之间的适配器，约 40 行。
/// </para>
/// </summary>
public sealed class SipUdpHandler : IUdpHandler
{
    private readonly ISipHandler _handler;
    private readonly SipServerOptions _options;
    private readonly ILogger<SipUdpHandler> _logger;

    public SipUdpHandler(ISipHandler handler, SipServerOptions options, ILogger<SipUdpHandler> logger)
    {
        _handler = handler;
        _options = options;
        _logger = logger;
    }

    public async Task OnDatagramAsync(UdpContext datagram, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = SipParser.Parse(datagram.Data.Span);

            _logger.LogDebug("SIP {Type} {Method}{StatusCode} 来自 {Remote}",
                request.Type, request.Method ?? "", request.StatusCode, datagram.Remote);

            var response = await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);

            if (response is not null)
                await datagram.ReplyAsync(response.ToBytes(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 解析失败只记录：UDP 无连接，不能"断开"任何人
            _logger.LogWarning(ex, "SIP 报文处理失败（{Remote}）", datagram.Remote);
        }
    }
}

/// <summary>SIP over TCP 的帧编解码：按 Content-Length 切帧（RFC 3261 §18.3）。</summary>
public sealed class SipTcpFrameCodec : IFrameCodec
{
    /// <inheritdoc />
    public int TryReadFrames(scoped Span<byte> buffer, int length, Action<ReadOnlySpan<byte>> onFrame)
    {
        var offset = 0;

        while (offset < length)
        {
            // 1) 找头部结束（\r\n\r\n）
            var window = buffer.Slice(offset, length - offset);
            var headerEnd = window.IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
                break;   // 头部还没收全

            var bodyStart = headerEnd + 4;

            // 2) 从头部解析 Content-Length
            var contentLength = ParseContentLength(window.Slice(0, headerEnd));

            // 3) 判断 body 是否收全
            if (length - offset - bodyStart < contentLength)
                break;

            var total = bodyStart + contentLength;
            onFrame(window.Slice(0, total));
            offset += total;
        }

        if (offset > 0)
            buffer.Slice(offset, length - offset).CopyTo(buffer);

        return length - offset;
    }

    private static int ParseContentLength(ReadOnlySpan<byte> header)
    {
        var marker = "Content-Length:"u8;
        var index = header.IndexOf(marker);

        if (index < 0)
            return 0;

        var value = header.Slice(index + marker.Length);
        var end = value.IndexOf((byte)'\r');
        if (end < 0)
            end = value.IndexOf((byte)'\n');
        if (end >= 0)
            value = value.Slice(0, end);

        // 头字段值是 ASCII 数字：直接转字符串解析（比手写数字解析更直观，且只在切帧时执行一次）
        return int.TryParse(System.Text.Encoding.ASCII.GetString(value).Trim(), out var length) ? length : 0;
    }
}
