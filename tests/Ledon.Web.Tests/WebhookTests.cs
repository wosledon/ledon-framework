using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ledon.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>出站 Webhook：订阅匹配、HMAC 签名、重试、死信记录。</summary>
public class WebhookTests : IAsyncLifetime
{
    private readonly FakeWebhookHandler _handler = new();
    private readonly List<ServiceProvider> _providers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }

    private async Task<(IServiceProvider Provider, IWebhookPublisher Publisher, IWebhookSubscriptionStore Subscriptions, IWebhookDeliveryStore Deliveries)> StartAsync(
        Action<LedonWebhookOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddLedonWebhooks(options =>
        {
            options.HttpMessageHandler = _handler;
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
            options.RequestTimeout = TimeSpan.FromSeconds(5);
            configure?.Invoke(options);
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return (
            provider,
            provider.GetRequiredService<IWebhookPublisher>(),
            provider.GetRequiredService<IWebhookSubscriptionStore>(),
            provider.GetRequiredService<IWebhookDeliveryStore>());
    }

    [Fact]
    public async Task Publishes_signed_payload_to_matching_subscription()
    {
        var (_, publisher, subscriptions, deliveries) = await StartAsync();

        subscriptions.Add(new WebhookSubscription
        {
            Url = "https://receiver.test/hooks",
            Secret = "top-secret",
            EventTypes = ["order.created"],
        });

        var queued = await publisher.PublishAsync("order.created", new { orderId = 42 });
        Assert.Equal(1, queued);

        var delivery = await WaitForDeliveryAsync(deliveries);
        Assert.True(delivery.Success);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(200, delivery.StatusCode);

        var captured = Assert.Single(_handler.Captured);
        Assert.Equal("order.created", captured.Event);
        Assert.Contains("\"orderId\":42", captured.Body);
        Assert.False(string.IsNullOrEmpty(captured.Signature));

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("top-secret"),
            Encoding.UTF8.GetBytes($"{captured.Timestamp}.{captured.Body}"));

        Assert.Equal($"sha256={Convert.ToHexString(expected).ToLowerInvariant()}", captured.Signature);
    }

    [Fact]
    public async Task Retries_until_success()
    {
        _handler.Respond = attempt => attempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;

        var (_, publisher, subscriptions, deliveries) = await StartAsync();

        subscriptions.Add(new WebhookSubscription
        {
            Url = "https://receiver.test/retry",
            EventTypes = ["*"],
        });

        await publisher.PublishAsync("user.created", new { userId = "u1" });

        var delivery = await WaitForDeliveryAsync(deliveries, () => _handler.Captured.Count);
        Assert.True(delivery.Success);
        Assert.Equal(2, delivery.Attempts);
        Assert.Equal(2, _handler.Captured.Count);
    }
    [Fact]
    public async Task Non_matching_or_disabled_subscriptions_are_skipped()
    {
        var (_, publisher, subscriptions, deliveries) = await StartAsync();

        subscriptions.Add(new WebhookSubscription
        {
            Url = "https://receiver.test/other",
            EventTypes = ["order.created"],
        });

        subscriptions.Add(new WebhookSubscription
        {
            Url = "https://receiver.test/disabled",
            EventTypes = ["user.created"],
            Enabled = false,
        });

        var queued = await publisher.PublishAsync("user.created", new { userId = "u1" });

        Assert.Equal(0, queued);
        await Task.Delay(50);
        Assert.Empty(_handler.Captured);
        Assert.Empty(deliveries.Recent());
    }

    [Fact]
    public async Task Failed_delivery_is_recorded_as_dead_letter_and_can_be_replayed()
    {
        _handler.Respond = _ => HttpStatusCode.InternalServerError;

        var (_, publisher, subscriptions, deliveries) = await StartAsync(o => o.MaxAttempts = 2);

        subscriptions.Add(new WebhookSubscription
        {
            Url = "https://receiver.test/dead",
            EventTypes = ["order.created"],
        });

        await publisher.PublishAsync("order.created", new { orderId = 7 });

        var failed = await WaitForDeliveryAsync(deliveries);
        Assert.False(failed.Success);
        Assert.Single(deliveries.DeadLetters());

        _handler.Respond = _ => HttpStatusCode.OK;

        Assert.True(await publisher.ReplayAsync(failed.Id));

        await WaitUntilAsync(
            () => deliveries.Recent().Any(delivery => delivery.Success),
            TimeSpan.FromSeconds(2));
    }

    private static async Task<WebhookDelivery> WaitForDeliveryAsync(
        IWebhookDeliveryStore deliveries,
        Func<int>? observed = null)
    {
        WebhookDelivery? delivery = null;

        await WaitUntilAsync(
            () => (delivery = deliveries.Recent().FirstOrDefault()) is not null,
            TimeSpan.FromSeconds(2));

        return delivery ?? throw new TimeoutException(
            $"等待 Webhook 投递记录超时（HTTP 尝试 {observed?.Invoke() ?? 0} 次）");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private sealed class FakeWebhookHandler : HttpMessageHandler
    {
        private int _attempt;

        public ConcurrentQueue<CapturedWebhook> Captured { get; } = new();

        public Func<int, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Captured.Enqueue(new CapturedWebhook(
                request.RequestUri!.ToString(),
                Header(request, "X-Ledon-Event"),
                Header(request, "X-Ledon-Delivery"),
                Header(request, "X-Ledon-Timestamp"),
                Header(request, "X-Ledon-Signature"),
                body));

            var attempt = Interlocked.Increment(ref _attempt);

            return new HttpResponseMessage(Respond(attempt));
        }

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";
    }

    private sealed record CapturedWebhook(
        string Url,
        string Event,
        string Delivery,
        string Timestamp,
        string Signature,
        string Body);
}
