namespace Ledon.Streaming;

/// <summary>流媒体选项。</summary>
public sealed class LedonStreamingOptions
{
    /// <summary>签名密钥（必填）。生产环境从配置中心/密钥管理注入，不要写死在代码里。</summary>
    public string Secret { get; set; } = "";

    /// <summary>令牌默认有效期，默认 10 分钟。</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>对外 RTMP 地址（拼推流 URL 用，如 <c>rtmp://media.example.com</c>）。</summary>
    public string? PublicRtmpUrl { get; set; }

    /// <summary>对外 HTTP 地址（FLV/HLS 用，如 <c>http://media.example.com</c>）。</summary>
    public string? PublicHttpUrl { get; set; }

    /// <summary>
    /// 未注册 <c>IStreamAuthorizer</c> 时的默认策略：拒绝还是放行。默认 <c>false</c>（fail-closed）。
    /// 仅在业务主动移除默认鉴权器（<c>RemoveAll&lt;IStreamAuthorizer&gt;()</c>）时生效。
    /// </summary>
    public bool AllowWhenNoAuthorizer { get; set; }

    /// <summary>流离线判定阈值：超过该时长没有心跳视为掉线，默认 90 秒。</summary>
    public TimeSpan OfflineThreshold { get; set; } = TimeSpan.FromSeconds(90);
}
