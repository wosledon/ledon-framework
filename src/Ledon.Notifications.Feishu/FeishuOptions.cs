namespace Ledon.Notifications.Feishu;

/// <summary>飞书配置。</summary>
public sealed class FeishuOptions
{
    /// <summary>应用 App ID（自建应用发消息必需）。</summary>
    public string? AppId { get; set; }

    /// <summary>应用 App Secret（换取 tenant_access_token）。</summary>
    public string? AppSecret { get; set; }

    /// <summary>
    /// 自定义机器人 Webhook 地址（群通知最简方式）。
    /// 形如 <c>https://open.feishu.cn/open-apis/bot/v2/hook/xxxx</c>。
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>机器人签名密钥（开启签名校验后由飞书提供；不配则不签名）。</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>开放平台地址，默认 <c>https://open.feishu.cn</c>（Lark 国际版用 <c>https://open.larksuite.com</c>）。</summary>
    public string BaseUrl { get; set; } = "https://open.feishu.cn";

    /// <summary>事件订阅的 Encrypt Key（配了才对回调内容解密）。</summary>
    public string? EncryptKey { get; set; }

    /// <summary>事件订阅的 Verification Token（校验回调来源）。</summary>
    public string? VerificationToken { get; set; }

    /// <summary>事件回调路由前缀，默认 <c>/feishu/events</c>。</summary>
    public string EventRoute { get; set; } = "/feishu/events";

    /// <summary>token 提前刷新的秒数（默认 300，避免临界过期）。</summary>
    public int TokenRefreshSkewSeconds { get; set; } = 300;
}
