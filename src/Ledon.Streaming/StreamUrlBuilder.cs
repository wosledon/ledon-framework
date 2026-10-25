using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Ledon.Streaming;

/// <summary>可播放/可推流的地址集合。</summary>
public sealed record StreamUrls(
    string? RtmpPublish,
    string? HttpFlvPlay,
    string? HlsPlay,
    string? WebRtcPlay,
    string? WhipPublish,
    string? WhepPlay);

/// <summary>推拉流地址构建器：统一拼媒体服务器的对外地址并附上鉴权令牌。</summary>
public sealed class StreamUrlBuilder(IStreamTokenService tokens, LedonStreamingOptions options){
    /// <summary>生成推流地址（RTMP）。</summary>
    public string BuildPublishUrl(StreamIdentity identity, TimeSpan? lifetime = null, string? extra = null)
    {
        var token = tokens.CreateToken(identity, StreamAction.Publish, lifetime, extra);

        return $"{options.PublicRtmpUrl?.TrimEnd('/')}/{identity.Path}?token={token.Value}";
    }

    /// <summary>生成 HTTP-FLV 播放地址。</summary>
    public string BuildFlvPlayUrl(StreamIdentity identity, TimeSpan? lifetime = null, string? extra = null)
    {
        var token = tokens.CreateToken(identity, StreamAction.Play, lifetime, extra);

        return $"{options.PublicHttpUrl?.TrimEnd('/')}/{identity.Path}.flv?token={token.Value}";
    }

    /// <summary>生成 HLS 播放地址。</summary>
    public string BuildHlsPlayUrl(StreamIdentity identity, TimeSpan? lifetime = null, string? extra = null)
    {
        var token = tokens.CreateToken(identity, StreamAction.Play, lifetime, extra);

        return $"{options.PublicHttpUrl?.TrimEnd('/')}/{identity.Path}.m3u8?token={token.Value}";
    }

    /// <summary>生成 WebRTC 播放地址。</summary>
    public string BuildWebRtcPlayUrl(StreamIdentity identity, TimeSpan? lifetime = null, string? extra = null)
    {
        var token = tokens.CreateToken(identity, StreamAction.Play, lifetime, extra);

        return $"{options.PublicWebRtcUrl?.TrimEnd('/')}/{identity.Path}?token={token.Value}";
    }

    /// <summary>一次性拿到全部地址（播放页用）。</summary>
    public StreamUrls BuildAll(StreamIdentity identity, TimeSpan? lifetime = null, string? extra = null) =>
        new(
            BuildPublishUrl(identity, lifetime, extra),
            BuildFlvPlayUrl(identity, lifetime, extra),
            BuildHlsPlayUrl(identity, lifetime, extra),
            BuildWebRtcPlayUrl(identity, lifetime, extra),
            BuildWhipUrl(identity, lifetime, extra),
            BuildWhepUrl(identity, lifetime, extra));

    /// <summary>WHIP 推流地址（WebRTC 推流，浏览器直接发布）。</summary>
    public string BuildWhipUrl(StreamIdentity identity, TimeSpan? lifetime = null, string? extra = null)
    {
        var token = tokens.CreateToken(identity, StreamAction.Publish, lifetime, extra);

        return $"{options.PublicHttpUrl?.TrimEnd('/')}{options.WhipRoute}/{identity.Path}?token={token.Value}";
    }

    /// <summary>WHEP 拉流地址（WebRTC 播放，浏览器原生播放）。</summary>
    public string BuildWhepUrl(StreamIdentity identity, TimeSpan? lifetime = null, string? extra = null)
    {
        var token = tokens.CreateToken(identity, StreamAction.Play, lifetime, extra);

        return $"{options.PublicHttpUrl?.TrimEnd('/')}{options.WhepRoute}/{identity.Path}?token={token.Value}";
    }
}

/// <summary>
/// 播放信息端点：给前端一条"这条流能用什么协议看"的答案（含带令牌的地址）。
/// 前端不必知道媒体服务器的地址结构。
/// </summary>
public sealed class StreamPlaybackEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        builder.MapGet("/streaming/playback/{app}/{name}", (
            string app,
            string name,
            StreamUrlBuilder urls,
            IStreamRegistry registry,
            HttpContext context) =>
        {
            var identity = new StreamIdentity(app, name);
            var status = registry.Find(identity);

            var extra = context.User?.Identity?.IsAuthenticated == true
                ? context.User.FindFirst("sub")?.Value
                : null;

            return Results.Ok(new
            {
                stream = identity.Path,
                online = status?.Online ?? false,
                players = status?.PlayerCount ?? 0,
                protocols = new
                {
                    flv = urls.BuildFlvPlayUrl(identity, extra: extra),
                    hls = urls.BuildHlsPlayUrl(identity, extra: extra),
                    webrtc = urls.BuildWebRtcPlayUrl(identity, extra: extra),
                    whep = urls.BuildWhepUrl(identity, extra: extra),
                },
            });
        });

        builder.MapGet("/streaming/streams", (IStreamRegistry registry) =>
            Results.Ok(registry.Snapshot().Select(status => new
            {
                stream = status.Identity.Path,
                online = status.Online,
                players = status.PlayerCount,
                publisher = status.PublisherId,
                protocol = status.PublishProtocol?.ToString(),
                startedAtUtc = status.StartedAtUtc,
            })));
    }
}
