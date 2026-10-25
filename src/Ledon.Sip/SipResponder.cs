using Ledon.Sip;

namespace Ledon.Sip;

/// <summary>SIP 服务端选项。</summary>
public sealed class LedonSipOptions
{
    /// <summary>监听端口，默认 5060。</summary>
    public int Port { get; set; } = 5060;

    /// <summary>User-Agent 头值，默认 <c>Ledon/1.0</c>。</summary>
    public string UserAgent { get; set; } = "Ledon/1.0";

    /// <summary>Server 头值，默认与 <see cref="UserAgent"/> 相同。</summary>
    public string Server { get; set; } = "Ledon/1.0";

    /// <summary>自动应答 OPTIONS（保活探测），默认 <c>true</c>。</summary>
    public bool AutoAnswerOptions { get; set; } = true;

    /// <summary>自动应答 REGISTER，默认 <c>false</c>（真实注册需鉴权与位置服务）。</summary>
    public bool AutoAnswerRegister { get; set; }
}

/// <summary>SIP 事件处理器。</summary>
public interface ISipHandler
{
    /// <summary>收到一条 SIP 消息；返回要发回的响应（<c>null</c> = 不应答）。</summary>
    Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认 SIP 应答器：OPTIONS 回 200 OK（保活）、REGISTER 可选回 200 OK。
/// 业务处理器实现 <see cref="ISipHandler"/> 即可接管（INVITE/BYE 等）。
/// </summary>
public sealed class SipResponder(LedonSipOptions options) : ISipHandler
{
    /// <summary>生成的 To tag 前缀（规范要求全局唯一 + 密码学随机）。</summary>
    private const string TagPrefix = "s-";

    public Task<SipMessage?> HandleAsync(
        SipMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Type != SipMessageType.Request || message.Method is null)
            return Task.FromResult<SipMessage?>(null);

        switch (message.Method)
        {
            case "OPTIONS" when options.AutoAnswerOptions:
                return Task.FromResult<SipMessage?>(Ok(message, options.UserAgent));

            case "REGISTER" when options.AutoAnswerRegister:
                return Task.FromResult<SipMessage?>(Ok(message, options.UserAgent));

            default:
                return Task.FromResult<SipMessage?>(null);
        }
    }

    /// <summary>
    /// 构造 200 OK。自动带上 Via/From/To/Call-ID/CSeq，并补上 To tag、
    /// Contact、Server、Content-Length——缺 To tag 的真实 UAC 会直接判为非法响应。
    /// </summary>
    public static SipMessage Ok(SipMessage request, string userAgent, string? server = null) =>
        Response(request, 200, "OK", userAgent, server);

    /// <summary>
    /// 按请求构造响应：复制事务必需的五个头，补 To tag / Contact / Server / Content-Length。
    /// </summary>
    public static SipMessage Response(
        SipMessage request,
        int statusCode,
        string reasonPhrase,
        string userAgent = "Ledon/1.0",
        string? server = null,
        string? body = null)
    {
        var response = new SipMessage
        {
            Type = SipMessageType.Response,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
        };

        // Via 必须原样、按顺序带回（多跳时顺序就是响应的回程路径）
        foreach (var via in request.HeaderValues("Via"))
            response.AddHeader("Via", via);

        response.AddHeader("From", request.From ?? "");

        // To tag：UAS 必须自己生成（请求里的 To 通常没有 tag）
        var to = request.To ?? "";
        if (!to.Contains("tag=", StringComparison.OrdinalIgnoreCase))
            to += $";tag={NewTag()}";

        response.AddHeader("To", to);
        response.AddHeader("Call-ID", request.CallId ?? "");
        response.AddHeader("CSeq", request.CSeq ?? "");
        response.AddHeader("User-Agent", userAgent);
        response.AddHeader("Server", server ?? userAgent);

        // Contact：告诉对端后续请求（ACK/BYE）发到哪里
        response.AddHeader("Contact", $"<{request.RequestUri}>");

        if (!string.IsNullOrEmpty(body))
            response.WithBody(body);

        return response;
    }

    /// <summary>生成 To tag（时间 + 随机，保证同一进程内不重复）。</summary>
    public static string NewTag() =>
        $"{TagPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}{Random.Shared.Next():x8}";
}
