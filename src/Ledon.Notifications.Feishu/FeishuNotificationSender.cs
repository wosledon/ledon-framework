using Ledon.Notifications;
using Microsoft.Extensions.Logging;

namespace Ledon.Notifications.Feishu;

/// <summary>
/// 飞书通知渠道：把 <see cref="Notification"/> 经飞书机器人/应用发出去。
/// 收件人是 <c>open_id</c>/<c>chat_id</c>/<c>user_id</c> 时走自建应用；
/// 元数据里带 <c>webhook</c> 时走自定义机器人（群通知）。
/// </summary>
public sealed class FeishuNotificationSender : INotificationSender, INotificationChannel
{
    /// <summary>渠道名。</summary>
    public const string ChannelName = "feishu";

    /// <inheritdoc />
    public string Channel => ChannelName;

    private readonly FeishuClient _client;
    private readonly FeishuOptions _options;
    private readonly ILogger<FeishuNotificationSender> _logger;

    public FeishuNotificationSender(FeishuClient client, FeishuOptions options, ILogger<FeishuNotificationSender> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<NotificationResult> SendAsync(
        Notification notification, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(notification.Channel, ChannelName, StringComparison.OrdinalIgnoreCase))
            return NotificationResult.Fail($"飞书发送器只处理 {ChannelName} 渠道（收到 {notification.Channel}）");

        try
        {
            // 元数据指定 receive_id_type 时按指定类型发，否则默认 open_id
            var idType = notification.Metadata?.TryGetValue("receive_id_type", out var type) == true
                ? type
                : "open_id";

            var messageId = await _client.SendToUserAsync(
                notification.Recipient,
                ComposeBody(notification),
                idType,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("飞书消息已发送到 {Recipient}（messageId={MessageId}）", notification.Recipient, messageId);

            return NotificationResult.Ok(messageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "飞书消息发送失败：{Recipient}", notification.Recipient);
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

    /// <summary>群机器人快捷方式：直接发文本到 Webhook（不依赖自建应用）。</summary>
    public Task<string> SendToWebhookAsync(string text, CancellationToken cancellationToken = default) =>
        _client.SendTextAsync(text, cancellationToken);

    private static string ComposeBody(Notification notification) =>
        string.IsNullOrWhiteSpace(notification.Subject)
            ? notification.Body
            : $"【{notification.Subject}】\n{notification.Body}";
}
