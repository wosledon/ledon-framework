using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Ledon.Notifications;

/// <summary>
/// SMTP 邮件发送（渠道实现之一）。短信/推送网关按同样模式实现
/// <see cref="INotificationSender"/> 或直接以 <c>Channel</c> 分支接入。
/// </summary>
public sealed class SmtpNotificationSender : INotificationSender, INotificationChannel
{
    /// <inheritdoc />
    public string Channel => "email";

    private readonly LedonNotificationOptions _options;
    private readonly ILogger _logger;

    // 日志可选：宿主没接日志时静默跳过（与模块系统一致）
    public SmtpNotificationSender(LedonNotificationOptions options, ILogger<SmtpNotificationSender>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<SmtpNotificationSender>.Instance;
    }

    public async Task<NotificationResult> SendAsync(
        Notification notification, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(notification.Channel, "email", StringComparison.OrdinalIgnoreCase))
            return NotificationResult.Fail($"SMTP 发送器只处理 email 渠道（收到 {notification.Channel}）");

        if (string.IsNullOrWhiteSpace(_options.SmtpHost))
            return NotificationResult.Fail("未配置 SmtpHost");

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(
                    _options.FromAddress ?? "noreply@localhost",
                    _options.FromDisplayName ?? "Ledon"),
                Subject = notification.Subject,
                Body = notification.Body,
                IsBodyHtml = false,
            };
            message.To.Add(notification.Recipient);

            using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
            {
                EnableSsl = _options.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            if (!string.IsNullOrWhiteSpace(_options.SmtpUserName))
                client.Credentials = new NetworkCredential(_options.SmtpUserName, _options.SmtpPassword);

            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("邮件已发送到 {Recipient}（主题：{Subject}）", notification.Recipient, notification.Subject);
            return NotificationResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 渠道错误不当异常抛给业务：记日志 + 返回失败结果（由重试策略决定下一步）
            _logger.LogError(ex, "邮件发送失败：{Recipient}", notification.Recipient);
            return NotificationResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyCollection<Notification> notifications, CancellationToken cancellationToken = default)
    {
        var results = new List<NotificationResult>(notifications.Count);

        foreach (var notification in notifications)
            results.Add(await SendAsync(notification, cancellationToken).ConfigureAwait(false));

        return results;
    }
}
