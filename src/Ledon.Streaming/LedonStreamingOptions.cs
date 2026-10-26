namespace Ledon.Streaming;

/// <summary>流媒体选项。</summary>
public sealed class LedonStreamingOptions
{
    /// <summary>签名密钥（必填）。生产环境从配置中心/密钥管理注入，不要写死在代码里。</summary>
    public string Secret { get; set; } = "";

    /// <summary>令牌默认有效期，默认 10 分钟。</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>媒体服务器类型，默认 SRS。</summary>
    /// <remarks>
    /// <see cref="MediaServerKind.ZlMediaKit"/> 目前是预留值：内置的
    /// <c>IMediaServerClient</c> 只实现了 SRS OpenAPI。接入 ZLMediaKit 需自行实现
    /// <c>IMediaServerClient</c> 并替换注册。
    /// </remarks>
    public MediaServerKind ServerKind { get; set; } = MediaServerKind.Srs;

    /// <summary>媒体服务器 API 地址（如 <c>http://127.0.0.1:1985</c>，SRS 的 OpenAPI）。</summary>
    public string? ServerApiUrl { get; set; }

    /// <summary>对外流媒体地址（拼推拉流 URL 用，如 <c>rtmp://media.example.com</c>）。</summary>
    public string? PublicRtmpUrl { get; set; }

    /// <summary>对外 HTTP 地址（FLV/HLS/WHIP/WHEP 用，如 <c>http://media.example.com</c>）。</summary>
    public string? PublicHttpUrl { get; set; }

    /// <summary>WebRTC 播放地址（如 <c>webrtc://media.example.com/live</c>）。</summary>
    public string? PublicWebRtcUrl { get; set; }

    /// <summary>
    /// 未注册 <c>IStreamAuthorizer</c> 时的默认策略：拒绝还是放行。默认 <c>false</c>（fail-closed）。
    /// 仅在业务主动移除默认鉴权器（<c>RemoveAll&lt;IStreamAuthorizer&gt;()</c>）时生效。
    /// </summary>
    public bool AllowWhenNoAuthorizer { get; set; }

    /// <summary>媒体服务器回调（webhook）路径，默认 <c>/streaming/hooks</c>。</summary>
    public string WebhookRoute { get; set; } = "/streaming/hooks";

    /// <summary>WHIP 推流端点路径，默认 <c>/streaming/whip</c>。</summary>
    public string WhipRoute { get; set; } = "/streaming/whip";

    /// <summary>WHEP 拉流端点路径，默认 <c>/streaming/whep</c>。</summary>
    public string WhepRoute { get; set; } = "/streaming/whep";

    /// <summary>流离线判定阈值：超过该时长没有心跳视为掉线，默认 90 秒。</summary>
    public TimeSpan OfflineThreshold { get; set; } = TimeSpan.FromSeconds(90);
}

/// <summary>媒体服务器类型。</summary>
public enum MediaServerKind
{
    /// <summary>SRS（简单实时服务器，支持 RTMP/WebRTC/WHIP/WHEP/HLS/HTTP-FLV）。</summary>
    Srs = 0,

    /// <summary>ZLMediaKit（预留：内置客户端尚未实现该服务的 OpenAPI）。</summary>
    ZlMediaKit = 1,
}
