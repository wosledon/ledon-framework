namespace Ledon.Notifications.Sms;

/// <summary>短信渠道选项。</summary>
public sealed class SmsOptions
{
    /// <summary>短信网关类型。</summary>
    public SmsProvider Provider { get; set; } = SmsProvider.Webhook;

    /// <summary>通用 Webhook 地址（Provider = Webhook 必填）。</summary>
    public string? Endpoint { get; set; }

    /// <summary>Bearer Token（Webhook 可选）。</summary>
    public string? AccessToken { get; set; }

    /// <summary>短信签名/发件号码。</summary>
    public string? From { get; set; }

    /// <summary>Twilio Account SID（Provider = Twilio 必填）。</summary>
    public string? AccountSid { get; set; }

    /// <summary>Twilio Auth Token（Provider = Twilio 必填）。</summary>
    public string? AuthToken { get; set; }

    /// <summary>HTTP 超时，默认 15 秒。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>自定义 HTTP 消息处理器（测试注入）。</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}

/// <summary>短信网关类型。</summary>
public enum SmsProvider
{
    /// <summary>通用 HTTP JSON Webhook：POST <c>{ to, from, body }</c>。</summary>
    Webhook = 0,

    /// <summary>Twilio Messages API。</summary>
    Twilio = 1,
}

/// <summary>短信发送结果。</summary>
public sealed record SmsSendResult(bool Success, string? MessageId = null, string? Error = null);

/// <summary>短信网关抽象（可替换为阿里云/腾讯云等实现）。</summary>
public interface ISmsGateway
{
    Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken = default);
}
