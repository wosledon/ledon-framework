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
    Task<SipMessage?> HandleAsync(SipMessage message, SipEndpoint endpoint, CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认 SIP 应答器：OPTIONS 回 200 OK（保活）、REGISTER 可选回 200 OK。
/// 业务处理器实现 <see cref="ISipHandler"/> 即可接管（INVITE/BYE 等）。
/// </summary>
public sealed class SipResponder(LedonSipOptions options) : ISipHandler
{
    public Task<SipMessage?> HandleAsync(
        SipMessage message, SipEndpoint endpoint, CancellationToken cancellationToken = default)
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
/// 帧边界按 <c>Content-Length</c> 切（UDP 数据报天然就是一条消息）。
/// </summary>
public sealed class SipEndpoint : BackgroundService
{
    private readonly LedonSipOptions _options;
    private readonly ISipHandler _handler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SipEndpoint> _logger;
    private readonly System.Net.Sockets.UdpClient _client;

    public SipEndpoint(
        LedonSipOptions options,
        ISipHandler handler,
        IServiceScopeFactory scopeFactory,
        ILogger<SipEndpoint> logger)
    {
        _options = options;
        _handler = handler;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _client = new System.Net.Sockets.UdpClient(options.Port);
    }

    /// <summary>当前绑定端口。</summary>
    public int BoundPort => ((System.Net.IPEndPoint)_client.Client.LocalEndPoint!).Port;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SIP 端点已监听 UDP {Port}（User-Agent={UserAgent}）", BoundPort, _options.UserAgent);

        while (!stoppingToken.IsCancellationRequested)
        {
            System.Net.Sockets.UdpReceiveResult received;
            try
            {
                received = await _client.ReceiveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var message = SipParser.Parse(received.Buffer);

                    _logger.LogDebug("SIP {Type} {Method}{StatusCode} 来自 {Remote}",
                        message.Type, message.Method ?? "", message.StatusCode, received.RemoteEndPoint);

                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<ISipHandler>();

                    var response = await handler.HandleAsync(message, this, CancellationToken.None).ConfigureAwait(false);

                    if (response is not null)
                        await ReplyAsync(response, received.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 解析失败只记录：UDP 无连接，不能"断开"任何人
                    _logger.LogWarning(ex, "SIP 报文处理失败（{Remote}）", received.RemoteEndPoint);
                }
            }, CancellationToken.None);
        }

        _client.Dispose();
    }

    /// <summary>发回一条 SIP 消息。</summary>
    public Task ReplyAsync(SipMessage message, System.Net.IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        _client.Send(message.ToBytes(), message.ToBytes().Length, remote);
        return Task.CompletedTask;
    }
}
