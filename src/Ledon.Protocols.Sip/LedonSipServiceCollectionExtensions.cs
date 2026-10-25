using Ledon.Protocols.Sip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SIP 支持入口：消息解析 + 自动应答（OPTIONS 保活 / 注册应答）。
/// <code>
/// builder.Services.AddLedonSip(o =&gt;
/// {
///     o.Port = 5060;
///     o.UserAgent = "Ledon/1.0";
/// });
/// </code>
/// SIP 走 UDP（默认 5060，数据报即一条消息）；TCP 场景用
/// <see cref="Ledon.Protocols.LengthPrefixedFrameCodec"/> 或分隔符编解码切帧。
/// </summary>
public static class LedonSipServiceCollectionExtensions
{
    /// <summary>注册 SIP 端点（UDP，默认 5060）。</summary>
    public static IServiceCollection AddLedonSip(this IServiceCollection services)
        => services.AddLedonSip(_ => { });

    /// <summary>注册 SIP 端点并配置。</summary>
    public static IServiceCollection AddLedonSip(
        this IServiceCollection services,
        Action<LedonSipOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonSipOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ISipHandler, SipResponder>();
        // SIP 端点构建在 UDP 服务端之上（处理器由容器解析）
        services.AddLedonProtocolUdpServer<SipHandler>(o => o.Port = options.Port);

        return services;
    }
}

/// <summary>SIP 选项。</summary>
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

/// <summary>
/// SIP 端点（UDP）：收报文 → 解析 → 交给 <see cref="ISipHandler"/> → 发回响应。
/// 构建在 <see cref="Ledon.Protocols.Server.UdpServer"/> 之上：协议实现只关心 SIP 语义，
/// 不管套接字、作用域与并发。
/// </summary>
public sealed class SipHandler : Ledon.Protocols.Server.IUdpHandler
{
    private readonly ISipHandler _handler;
    private readonly Microsoft.Extensions.Logging.ILogger<SipHandler> _logger;

    public SipHandler(ISipHandler handler, Microsoft.Extensions.Logging.ILogger<SipHandler> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    public async Task OnDatagramAsync(Ledon.Protocols.Server.UdpContext datagram, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = SipParser.Parse(datagram.Data.Span);

            _logger.LogDebug("SIP {Type} {Method}{StatusCode} 来自 {Remote}",
                message.Type, message.Method ?? "", message.StatusCode, datagram.Remote);

            var response = await _handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);

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
