using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Notifications;

/// <summary>
/// 多渠道路由发送器：按 <see cref="Notification.Channel"/> 选渠道实现
/// （渠道实现 <see cref="INotificationChannel"/> 声明自己处理哪个渠道），
/// 并统一记录发送日志、按 <see cref="LedonNotificationOptions.MaxRetries"/> 重试。
/// 新渠道（飞书/短信/推送）实现 <see cref="INotificationChannel"/> 并注册进容器，路由无需改动。
/// </summary>
public sealed class ChannelRoutingNotificationSender : INotificationSender
{
    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly LedonNotificationOptions _options;
    private readonly INotificationLog _log;
    private readonly ILogger _logger;

    public ChannelRoutingNotificationSender(
        IEnumerable<INotificationChannel> channels,
        LedonNotificationOptions options,
        INotificationLog log,
        ILogger<ChannelRoutingNotificationSender>? logger = null)
    {
        _channels = channels.ToArray();
        _options = options;
        _log = log;
        _logger = logger ?? NullLogger<ChannelRoutingNotificationSender>.Instance;
    }

    public async Task<NotificationResult> SendAsync(
        Notification notification, CancellationToken cancellationToken = default)
    {
        var channel = ResolveChannel(notification.Channel);
        if (channel is null)
        {
            var unknown = NotificationResult.Fail(
                $"未注册的渠道：{notification.Channel}（已注册：{string.Join(", ", RegisteredChannels())}）");
            _log.Record(notification, unknown, 0);
            return unknown;
        }

        NotificationResult result = NotificationResult.Fail("未发送");
        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxRetries); attempt++)
        {
            result = await channel.SendAsync(notification, cancellationToken).ConfigureAwait(false);

            if (result.Accepted)
                break;

            if (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(
                    "通知发送失败（{Channel} → {Recipient}，第 {Attempt} 次）：{Error}",
                    notification.Channel, notification.Recipient, attempt, result.Error);

                // 指数退避：1s、2s、4s……渠道抖动通常几次内恢复
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            }
        }

        _log.Record(notification, result, _options.MaxRetries);
        return result;
    }

    public async Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyCollection<Notification> notifications, CancellationToken cancellationToken = default)
    {
        var results = new List<NotificationResult>(notifications.Count);

        foreach (var notification in notifications)
            results.Add(await SendAsync(notification, cancellationToken).ConfigureAwait(false));

        return results;
    }

    private INotificationSender? ResolveChannel(string channel)
    {
        // 渠道名精确匹配；未注册的渠道直接失败（fail-closed，不静默走错渠道）
        return _channels.FirstOrDefault(candidate =>
            string.Equals(candidate.Channel, channel, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> RegisteredChannels() =>
        _channels.Select(channel => channel.Channel);
}
