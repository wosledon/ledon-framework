using System.Text.Json;
using System.Text.Json.Serialization;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Streaming;

/// <summary>
/// 媒体服务器 webhook 端点：接收 on_publish / on_play / on_unpublish / on_stop / dvr 回调，
/// 先鉴权（令牌 + 授权策略）再放行，并把事件分发给 <see cref="IStreamEventHandler"/>。
/// <para>
/// SRS 期望返回 <c>{"code":0}</c> 放行、非 0 拒绝（拒绝时媒体服务器会断开该客户端）。
/// </para>
/// </summary>
public sealed class StreamWebhookEndpoint(LedonStreamingOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        builder.MapPost(options.WebhookRoute, async (HttpContext context, StreamEventDispatcher dispatcher, CancellationToken ct) =>
        {
            var payload = await JsonSerializer.DeserializeAsync<SrsWebhookPayload>(
                context.Request.Body,
                cancellationToken: ct).ConfigureAwait(false);

            if (payload is null || string.IsNullOrWhiteSpace(payload.Action) || string.IsNullOrWhiteSpace(payload.Stream))
                return Results.Json(new { code = 1 }, statusCode: StatusCodes.Status400BadRequest);

            var action = MapAction(payload.Action);
            if (action is null)
                return Results.Json(new { code = 0 });   // 不关心的事件类型直接放行

            var identity = StreamIdentity.Parse(payload.Stream!, payload.Params);
            var authContext = new StreamAuthContext
            {
                Identity = identity,
                Action = action.Value,
                Protocol = StreamEventDispatcher.ParseProtocolName(payload.Protocol),
                ClientIp = payload.Ip,
                ClientId = payload.ClientId,
                Token = payload.Params?.TryGetValue("token", out var token) == true ? token : null,
                Parameters = payload.Params,
            };

            var allowed = await dispatcher.AuthorizeAsync(authContext, ct).ConfigureAwait(false);
            if (!allowed.Allowed)
            {
                return Results.Json(new { code = 1, message = allowed.Reason });
            }

            await dispatcher.DispatchAsync(action.Value, identity, payload.ClientId, payload.Ip, payload.Protocol, payload.Params, ct)
                .ConfigureAwait(false);

            return Results.Json(new { code = 0 });
        });
    }

    private static StreamAction? MapAction(string action) => action switch
    {
        "on_publish" => StreamAction.Publish,
        "on_play" => StreamAction.Play,
        _ => null,
    };

    private sealed class SrsWebhookPayload
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("stream")]
        public string? Stream { get; set; }

        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("vhost")]
        public string? Vhost { get; set; }

        [JsonPropertyName("param")]
        public Dictionary<string, string>? Params { get; set; }

        /// <summary>协议（SRS 不总是给，缺省按 RTMP 处理）。</summary>
        public string? Protocol => Params?.TryGetValue("protocol", out var protocol) == true ? protocol : null;
    }
}

/// <summary>事件分发与鉴权编排。</summary>
public sealed class StreamEventDispatcher
{
    private readonly IStreamAuthorizer? _authorizer;
    private readonly IStreamRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<StreamEventDispatcher> _logger;
    private readonly LedonStreamingOptions _options;

    public StreamEventDispatcher(
        IStreamRegistry registry,
        IServiceProvider services,
        ILogger<StreamEventDispatcher> logger,
        LedonStreamingOptions options,
        IStreamAuthorizer? authorizer = null)
    {
        _authorizer = authorizer;
        _registry = registry;
        _services = services;
        _logger = logger;
        _options = options;
    }

    /// <summary>鉴权（供端点/中间件调用）。未注册鉴权器时按 <see cref="LedonStreamingOptions.AllowWhenNoAuthorizer"/> 决定。</summary>
    public ValueTask<StreamAuthResult> AuthorizeAsync(StreamAuthContext context, CancellationToken cancellationToken = default)
    {
        if (_authorizer is not null)
            return _authorizer.AuthorizeAsync(context, cancellationToken);

        return ValueTask.FromResult(_options.AllowWhenNoAuthorizer
            ? StreamAuthResult.Allow()
            : StreamAuthResult.Deny("未配置流鉴权器（AllowWhenNoAuthorizer = false）"));
    }

    /// <summary>更新注册表并分发事件。</summary>
    public async Task DispatchAsync(
        StreamAction action,
        StreamIdentity identity,
        string? clientId,
        string? clientIp,
        string? protocol,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken = default)
    {
        var parsedProtocol = ParseProtocolName(protocol);

        // 1) 注册表先更新（前端查状态不等事件处理器）
        switch (action)
        {
            case StreamAction.Publish:
                _registry.OnPublish(identity, clientId, clientIp, parsedProtocol, parameters);
                break;

            case StreamAction.Play:
                _registry.OnPlay(identity, clientId);
                break;
        }

        // 2) 分发事件：单个处理器失败只记录，不影响其他处理器与媒体流
        using var scope = _services.CreateScope();

        foreach (var handler in scope.ServiceProvider.GetServices<IStreamEventHandler>())
        {
            try
            {
                await handler.HandleAsync(new StreamEvent
                {
                    Type = action == StreamAction.Publish ? StreamEventType.Publish : StreamEventType.Play,
                    Identity = identity,
                    ClientId = clientId,
                    ClientIp = clientIp,
                    Protocol = parsedProtocol,
                    Parameters = parameters,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "流事件处理器 {Handler} 失败（{Stream}）", handler.GetType().Name, identity);
            }
        }
    }

    internal static StreamProtocol? ParseProtocolName(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "rtmp" => StreamProtocol.Rtmp,
        "http-flv" or "flv" => StreamProtocol.HttpFlv,
        "hls" => StreamProtocol.Hls,
        "webrtc" => StreamProtocol.WebRtc,
        "rtsp" => StreamProtocol.Rtsp,
        "srt" => StreamProtocol.Srt,
        _ => null,
    };
}
