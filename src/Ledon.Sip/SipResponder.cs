using Ledon.Sip;

namespace Ledon.Sip;

public sealed class LedonSipOptions
{
    /// <summary>监听端口，默认 5060。</summary>
    public int Port { get; set; } = 5060;

    /// <summary>User-Agent 头值，默认 <c>Ledon/1.0</c>。</summary>
    public string UserAgent { get; set; } = "Ledon/1.0";

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
    public Task<SipMessage?> HandleAsync(
        SipMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Type != SipMessageType.Request || message.Method is null)
            return Task.FromResult<SipMessage?>(null);

        switch (message.Method)
        {
            case "OPTIONS" when options.AutoAnswerOptions:
                return Task.FromResult<SipMessage?>(SipResponder.Ok(message, options.UserAgent));

            case "REGISTER" when options.AutoAnswerRegister:
                return Task.FromResult<SipMessage?>(SipResponder.Ok(message, options.UserAgent));

            default:
                return Task.FromResult<SipMessage?>(null);
        }
    }

    /// <summary>构造 200 OK（自动带上 Via/From/To/Call-ID/CSeq）。</summary>
    public static SipMessage Ok(SipMessage request, string userAgent) =>
        SipParser.CreateResponse(200, "OK", new Dictionary<string, string>
        {
            ["Via"] = request.Via ?? "",
            ["From"] = request.From ?? "",
            ["To"] = request.To ?? "",
            ["Call-ID"] = request.CallId ?? "",
            ["CSeq"] = request.CSeq ?? "",
            ["User-Agent"] = userAgent,
            ["Content-Length"] = "0",
        });
}
