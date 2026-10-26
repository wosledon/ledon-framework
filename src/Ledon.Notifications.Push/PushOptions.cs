namespace Ledon.Notifications.Push;

/// <summary>推送渠道选项。</summary>
public sealed class PushOptions
{
    /// <summary>推送网关类型。</summary>
    public PushProvider Provider { get; set; } = PushProvider.Webhook;

    /// <summary>通用 Webhook 地址（Provider = Webhook 必填）。</summary>
    public string? Endpoint { get; set; }

    /// <summary>Webhook Bearer Token（可选）。</summary>
    public string? AccessToken { get; set; }

    /// <summary>FCM 项目 ID（Provider = Fcm 必填）。</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// FCM OAuth2 Access Token。可用 <see cref="AccessTokenProvider"/> 动态获取/刷新；
    /// 两者都为空时发送失败。
    /// </summary>
    public Func<CancellationToken, Task<string>>? AccessTokenProvider { get; set; }

    /// <summary>HTTP 超时，默认 15 秒。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>自定义 HTTP 消息处理器（测试注入）。</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}

/// <summary>推送网关类型。</summary>
public enum PushProvider
{
    /// <summary>通用 HTTP JSON Webhook：POST <c>{ token, title, body, data }</c>。</summary>
    Webhook = 0,

    /// <summary>Firebase Cloud Messaging HTTP v1。</summary>
    Fcm = 1,
}

/// <summary>推送发送结果。</summary>
public sealed record PushSendResult(bool Success, string? MessageId = null, string? Error = null);

/// <summary>推送网关抽象（可替换为 APNs/极光等实现）。</summary>
public interface IPushGateway
{
    Task<PushSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken cancellationToken = default);
}
