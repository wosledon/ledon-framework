namespace Ledon.Notifications;

/// <summary>待发送的通知（渠道无关的统一载荷）。</summary>
/// <param name="Channel">渠道名：<c>email</c> / <c>sms</c> / <c>push</c>。</param>
/// <param name="Recipient">接收方：邮箱 / 手机号 / 设备标识。</param>
/// <param name="Subject">主题（邮件用）。</param>
/// <param name="Body">正文（已渲染）。</param>
/// <param name="TemplateName">使用的模板名（发送记录里追溯用）。</param>
/// <param name="Metadata">附加元数据（随发送记录留存，便于排查）。</param>
public sealed record Notification(
    string Channel,
    string Recipient,
    string Subject,
    string Body,
    string? TemplateName = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>发送结果。</summary>
public sealed record NotificationResult(bool Accepted, string? ProviderMessageId = null, string? Error = null)
{
    /// <summary>已受理（投交渠道，不等于最终送达）。</summary>
    public static NotificationResult Ok(string? providerMessageId = null) => new(true, providerMessageId);

    /// <summary>被拒绝/失败。</summary>
    public static NotificationResult Fail(string error) => new(false, Error: error);
}

/// <summary>
/// 通知发送入口。业务只发"通知"，不关心走 SMTP、短信网关还是推送：
/// <code>
/// public class WelcomeMessage(INotificationSender sender)
/// {
///     public Task SendAsync(string email, CancellationToken ct) =&gt;
///         sender.SendAsync(new Notification("email", email, "欢迎", "感谢注册"), ct);
/// }
/// </code>
/// 发送默认经持久化队列（<c>AddLedonNotifications(o =&gt; o.UseDurableQueue = true)</c>）：
/// 渠道抖动只重试，进程重启不丢待发通知。
/// </summary>
public interface INotificationSender
{
    /// <summary>发送单条。</summary>
    Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>批量发送（逐条独立结果，一条失败不影响其余）。</summary>
    Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyCollection<Notification> notifications, CancellationToken cancellationToken = default);
}
