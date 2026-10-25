using System.Net.Http.Headers;
using System.Text;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Ledon.Streaming;

/// <summary>
/// WHIP / WHEP 端点：WebRTC 推流与拉流的标准信令（浏览器原生，无需插件）。
/// <list type="bullet">
///   <item>WHIP（推流）：客户端 POST SDP Offer → 服务端转发媒体服务器 → 返回 SDP Answer；</item>
///   <item>WHEP（拉流）：客户端 POST SDP Offer → 返回该流的 SDP Answer；</item>
///   <item>DELETE：结束会话。</item>
/// </list>
/// 媒体面由媒体服务器（SRS/ZLMediaKit）承担，本端点只做鉴权 + SDP 交换。
/// </summary>
public sealed class WhipWhepEndpoints(LedonStreamingOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        // WHIP：发布
        builder.MapPost($"{options.WhipRoute}/{{app}}/{{name}}", async (
            string app,
            string name,
            HttpRequest request,
            StreamEventDispatcher dispatcher,
            IMediaServerClient server,
            ILogger<WhipWhepEndpoints> logger,
            CancellationToken ct) =>
        {
            var identity = new StreamIdentity(app, name);
            var token = request.Query["token"];

            var allowed = await dispatcher.AuthorizeAsync(new StreamAuthContext
            {
                Identity = identity,
                Action = StreamAction.Publish,
                Protocol = StreamProtocol.WebRtc,
                ClientIp = request.HttpContext.Connection.RemoteIpAddress?.ToString(),
                Token = token,
                Parameters = request.Query.ToDictionary(entry => entry.Key, entry => entry.Value.ToString()),
            }, ct).ConfigureAwait(false);

            if (!allowed.Allowed)
                return Results.Unauthorized();

            var offer = await ReadSdpAsync(request).ConfigureAwait(false);

            // SDP 交换：转发给媒体服务器（SRS 的 WHIP 实现）
            var answer = await ExchangeSdpAsync(server, identity, offer, publish: true, ct).ConfigureAwait(false);
            if (answer is null)
            {
                logger.LogError("WHIP SDP 交换失败（{Stream}）：媒体服务器未返回应答", identity);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            return Results.Text(answer, "application/sdp", Encoding.UTF8, StatusCodes.Status201Created);
        });

        // WHEP：播放
        builder.MapPost($"{options.WhepRoute}/{{app}}/{{name}}", async (
            string app,
            string name,
            HttpRequest request,
            StreamEventDispatcher dispatcher,
            IMediaServerClient server,
            ILogger<WhipWhepEndpoints> logger,
            CancellationToken ct) =>
        {
            var identity = new StreamIdentity(app, name);
            var token = request.Query["token"];

            var allowed = await dispatcher.AuthorizeAsync(new StreamAuthContext
            {
                Identity = identity,
                Action = StreamAction.Play,
                Protocol = StreamProtocol.WebRtc,
                ClientIp = request.HttpContext.Connection.RemoteIpAddress?.ToString(),
                Token = token,
                Parameters = request.Query.ToDictionary(entry => entry.Key, entry => entry.Value.ToString()),
            }, ct).ConfigureAwait(false);

            if (!allowed.Allowed)
                return Results.Unauthorized();

            var offer = await ReadSdpAsync(request).ConfigureAwait(false);

            var answer = await ExchangeSdpAsync(server, identity, offer, publish: false, ct).ConfigureAwait(false);
            if (answer is null)
            {
                logger.LogError("WHEP SDP 交换失败（{Stream}）", identity);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            return Results.Text(answer, "application/sdp", Encoding.UTF8, StatusCodes.Status201Created);
        });

        // 结束会话（WHIP/WHEP 的 DELETE）
        builder.MapMethods(
            $"{options.WhipRoute}/{{app}}/{{name}}",
            ["DELETE"],
            (string app, string name) => Results.Ok());

        builder.MapMethods(
            $"{options.WhepRoute}/{{app}}/{{name}}",
            ["DELETE"],
            (string app, string name) => Results.Ok());
    }

    private static async Task<string> ReadSdpAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);

        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>把 SDP Offer 转发给媒体服务器的 WHIP 端点，拿回 SDP Answer。</summary>
    private static async Task<string?> ExchangeSdpAsync(
        IMediaServerClient server,
        StreamIdentity identity,
        string offer,
        bool publish,
        CancellationToken cancellationToken)
    {
        var http = server as IWhilpExchange ?? null;

        return http is null ? null : await http.ExchangeAsync(identity, offer, publish, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>SDP 交换能力（由媒体服务器客户端实现；未实现时 WHIP/WHEP 返回 502）。</summary>
public interface IWhilpExchange
{
    /// <summary>转发 SDP Offer，返回 SDP Answer。</summary>
    Task<string?> ExchangeAsync(StreamIdentity identity, string offer, bool publish, CancellationToken cancellationToken = default);
}
