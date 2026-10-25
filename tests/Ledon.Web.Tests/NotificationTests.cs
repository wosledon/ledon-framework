using Ledon.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>通知发送：渠道路由、重试、日志、批量。</summary>
public class NotificationTests
{
    private static ChannelRoutingNotificationSender Build(
        INotificationSender channel,
        int maxRetries = 1,
        INotificationLog? log = null) =>
        new(channel, new LedonNotificationOptions { MaxRetries = maxRetries }, log ?? new InMemoryNotificationLog(), NullLogger<ChannelRoutingNotificationSender>.Instance);

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

    private sealed class FakeChannel(string channel, int failuresBeforeSuccess = 0) : INotificationSender
    {
        private int _failures;

        public int Attempts { get; private set; }

        public int Sent { get; private set; }

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
}
