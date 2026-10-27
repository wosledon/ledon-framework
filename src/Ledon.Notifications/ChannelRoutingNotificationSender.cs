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
    /// <summary>已发送过的去重键 → 发送时刻（进程内；多实例部署需换共享存储）。</summary>
    private readonly Dictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);
    private readonly Lock _dedupeGate = new();

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
        // 去重：同一业务键只发一次（兜住"渠道已送达但返回失败"造成的重复）
        if (_options.DeduplicationKey is { } keyOf && IsDuplicate(keyOf(notification)))
        {
            _logger.LogDebug("通知去重命中，跳过重复发送");

            return NotificationResult.Ok();
        }

        var channel = ResolveChannel(notification.Channel);
        if (channel is null)
        {
            var unknown = NotificationResult.Fail(
                $"未注册的渠道：{notification.Channel}（已注册：{string.Join(", ", RegisteredChannels())}）");
            _log.Record(notification, unknown, 0);
            return unknown;
        }

        NotificationResult result = NotificationResult.Fail("未发送");
        var attempts = 0;

        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxRetries); attempt++)
        {
            attempts = attempt;
            result = await channel.SendAsync(notification, cancellationToken).ConfigureAwait(false);

            if (result.Accepted)
                break;

            if (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(
                    "通知发送失败（第 {Attempt} 次）", attempt);

                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        // 记录真实尝试次数（之前恒为 MaxRetries，首次成功也记 3，排查时没法判断）
        _log.Record(notification, result, attempts);

        if (result.Accepted && _options.DeduplicationKey is { } succeeded && succeeded(notification) is { } key)
            MarkSent(key);

        return result;
    }

    public async Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyCollection<Notification> notifications, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        var results = new NotificationResult[notifications.Count];

        // 有界并发：通知是 IO 密集型，串行发送 100 条要约 100 倍延迟
        var concurrency = Math.Max(1, _options.BatchConcurrency);

        if (concurrency == 1)
        {
            var index = 0;
            foreach (var notification in notifications)
                results[index++] = await SendAsync(notification, cancellationToken).ConfigureAwait(false);

            return results;
        }

        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var tasks = notifications.Select(async notification =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SendAsync(notification, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        for (var i = 0; i < results.Length; i++)
            results[i] = tasks[i].Result;

        return results;
    }

    /// <summary>指数退避（封顶）+ 可选抖动。</summary>
    private TimeSpan Backoff(int attempt)
    {
        var ticks = _options.RetryBaseDelay.Ticks * (1L << Math.Min(attempt - 1, 16));
        var delay = TimeSpan.FromTicks(Math.Min(ticks, _options.RetryMaxDelay.Ticks));

        if (!_options.RetryJitter)
            return delay;

        var jitter = 0.75 + (Random.Shared.NextDouble() * 0.5);   // ±25%

        return TimeSpan.FromTicks((long)(delay.Ticks * jitter));
    }

    private bool IsDuplicate(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        var cutoff = DateTimeOffset.UtcNow - _options.DeduplicationWindow;

        lock (_dedupeGate)
        {
            if (_sent.TryGetValue(key, out var sentAt) && sentAt >= cutoff)
                return true;

            // 顺手清理过期项，避免字典无限增长
            foreach (var stale in _sent.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
                _sent.Remove(stale);

            return false;
        }
    }

    private void MarkSent(string key)
    {
        lock (_dedupeGate)
            _sent[key] = DateTimeOffset.UtcNow;
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
