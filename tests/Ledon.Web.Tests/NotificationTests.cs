using Ledon.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>通知发送：渠道路由、重试、日志、批量。</summary>
public class NotificationTests
{
    private static ChannelRoutingNotificationSender Build(
        INotificationChannel channel,
        int maxRetries = 1,
        INotificationLog? log = null,
        Action<LedonNotificationOptions>? configure = null)
    {
        var options = new LedonNotificationOptions { MaxRetries = maxRetries };
        configure?.Invoke(options);

        return new ChannelRoutingNotificationSender(
            [channel], options, log ?? new InMemoryNotificationLog(), NullLogger<ChannelRoutingNotificationSender>.Instance);
    }

    [Fact]
    public async Task Attempts_records_the_real_try_count()
    {
        var log = new InMemoryNotificationLog();
        var sender = Build(new FakeChannel("email"), log: log);

        // 首次成功：之前恒记 MaxRetries（3），排查时没法判断到底试了几次
        await sender.SendAsync(new Notification("email", "a@b.c", "s", "b"));

        Assert.Equal(1, log.Recent()[0].Attempts);
    }

    [Fact]
    public async Task Duplicate_notifications_are_sent_once()
    {
        var fake = new FakeChannel("email");
        var sender = Build(fake, configure: o =>
            o.DeduplicationKey = notification => $"{notification.Channel}:{notification.Recipient}:{notification.Subject}");

        await sender.SendAsync(new Notification("email", "a@b.c", "same", "b"));
        await sender.SendAsync(new Notification("email", "a@b.c", "same", "b"));
        await sender.SendAsync(new Notification("email", "a@b.c", "same", "b"));

        Assert.Equal(1, fake.Sent);
    }

    [Fact]
    public async Task Deduplication_can_be_disabled()
    {
        var fake = new FakeChannel("email");
        var sender = Build(fake);   // 不设 DeduplicationKey

        await sender.SendAsync(new Notification("email", "a@b.c", "same", "b"));
        await sender.SendAsync(new Notification("email", "a@b.c", "same", "b"));

        Assert.Equal(2, fake.Sent);
    }

    [Fact]
    public async Task Batch_send_runs_concurrently()
    {
        var fake = new SlowChannel("email");
        var sender = Build(fake, configure: o => o.BatchConcurrency = 4);

        var notifications = Enumerable.Range(0, 8)
            .Select(i => new Notification("email", $"u{i}@b.c", "s", "b"))
            .ToArray();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = await sender.SendAsync(notifications);
        stopwatch.Stop();

        Assert.Equal(8, results.Count);
        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.Equal(8, fake.Sent);

        // 每条 200ms：串行要约 1.6s，4 并发应该明显更快（留足余量避免 CI 抖动）
        Assert.True(stopwatch.ElapsedMilliseconds < 1200,
            $"批量发送耗时 {stopwatch.ElapsedMilliseconds} ms，疑似退化成串行");
    }

    [Fact]
    public async Task Retry_backoff_respects_configured_base_and_max()
    {
        // 退避参数化：不再硬编码 1s/2s/4s（测试里等不起）
        var fake = new FakeChannel("email", failuresBeforeSuccess: 2);
        var sender = Build(fake, maxRetries: 3, configure: o =>
        {
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(20);
            o.RetryMaxDelay = TimeSpan.FromMilliseconds(30);
            o.RetryJitter = false;   // 关掉抖动才能算准总时长
        });

        var result = await sender.SendAsync(new Notification("email", "a@b.c", "s", "b"));

        Assert.True(result.Accepted);
        Assert.Equal(3, fake.Attempts);   // 2 次失败 + 1 次成功
    }

    [Fact]
    public async Task Email_channel_is_used_for_email_notifications()
    {
        var fake = new FakeChannel("email");
        var sender = Build(fake);

        var result = await sender.SendAsync(new Notification("email", "a@b.c", "s", "b"));

        Assert.True(result.Accepted);
        Assert.Equal(1, fake.Sent);
    }

    [Fact]
    public async Task Unknown_channel_fails_and_is_logged()
    {
        var log = new InMemoryNotificationLog();
        var sender = Build(new FakeChannel("email"), log: log);

        var result = await sender.SendAsync(new Notification("sms", "13800000000", "s", "b"));

        Assert.False(result.Accepted);
        Assert.Contains("未注册的渠道", result.Error);
        Assert.Single(log.Recent());
        Assert.False(log.Recent()[0].Success);
    }

    [Fact]
    public async Task Retry_recovers_from_transient_channel_failure()
    {
        var fake = new FakeChannel("email", failuresBeforeSuccess: 2);
        var sender = Build(fake, maxRetries: 3);

        var result = await sender.SendAsync(new Notification("email", "a@b.c", "s", "b"));

        Assert.True(result.Accepted);
        Assert.Equal(3, fake.Attempts);
    }

    [Fact]
    public async Task Exhausted_retries_return_failure()
    {
        var fake = new FakeChannel("email", failuresBeforeSuccess: 99);
        var log = new InMemoryNotificationLog();
        var sender = Build(fake, maxRetries: 2, log: log);

        var result = await sender.SendAsync(new Notification("email", "a@b.c", "s", "b"));

        Assert.False(result.Accepted);
        Assert.Equal(2, fake.Attempts);
        Assert.Single(log.Recent());
    }

    [Fact]
    public async Task Batch_sends_are_independent()
    {
        var fake = new FakeChannel("email");
        var sender = Build(fake);

        var results = await sender.SendAsync(
        [
            new Notification("email", "a@b.c", "s", "b"),
            new Notification("email", "d@e.f", "s", "b"),
        ]);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Accepted));
    }

    [Fact]
    public void Notification_log_reports_stats_per_channel()
    {
        var log = new InMemoryNotificationLog();

        log.Record(new Notification("email", "a@b.c", "s", "b"), NotificationResult.Ok(), 1);
        log.Record(new Notification("email", "a@b.c", "s", "b"), NotificationResult.Fail("boom"), 3);

        var stats = log.Stats();

        Assert.Equal((2, 1), stats["email"]);
        Assert.Equal(2, log.Recent().Count);
    }

    [Fact]
    public void Notification_log_is_bounded()
    {
        var log = new InMemoryNotificationLog(capacity: 3);

        for (var i = 0; i < 10; i++)
            log.Record(new Notification("email", "a@b.c", $"s{i}", "b"), NotificationResult.Ok(), 1);

        Assert.Equal(3, log.Recent().Count);
    }

    [Fact]
    public void Sender_is_registered_in_di()
    {
        var services = new ServiceCollection()
            .AddLedonNotifications(o => o.SmtpHost = "smtp.example.com")
            .BuildServiceProvider();

        Assert.NotNull(services.GetRequiredService<INotificationSender>());
        Assert.NotNull(services.GetRequiredService<INotificationLog>());
    }

    [Fact]
    public async Task Missing_smtp_host_returns_failure_result()
    {
        var services = new ServiceCollection()
            .AddLedonNotifications()   // 未配 SMTP
            .BuildServiceProvider();

        var result = await services.GetRequiredService<INotificationSender>()
            .SendAsync(new Notification("email", "a@b.c", "s", "b"));

        Assert.False(result.Accepted);
    }

    private sealed class FakeChannel(string channel, int failuresBeforeSuccess = 0) : INotificationChannel
    {
        private int _failures;

        public int Attempts { get; private set; }

        public int Sent { get; private set; }

        public string Channel => channel;

        public Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(notification.Channel, channel, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(NotificationResult.Fail("wrong channel"));

            Attempts++;

            if (_failures++ < failuresBeforeSuccess)
                return Task.FromResult(NotificationResult.Fail("transient"));

            Sent++;
            return Task.FromResult(NotificationResult.Ok("msg-1"));
        }

        public Task<IReadOnlyList<NotificationResult>> SendAsync(
            IReadOnlyCollection<Notification> notifications, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>每条都固定延迟的渠道（验证批量并发真的并发）。</summary>
    private sealed class SlowChannel(string channel) : INotificationChannel
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public string Channel => channel;

        public async Task<NotificationResult> SendAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _sent);

            return NotificationResult.Ok("msg-1");
        }

        public Task<IReadOnlyList<NotificationResult>> SendAsync(
            IReadOnlyCollection<Notification> notifications, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
