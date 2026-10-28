using System.Net;
using Ledon.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>Webhook 边界：参数校验、未知重放、订阅匹配、历史裁剪。</summary>
public class WebhookEdgeTests
{
    [Fact]
    public async Task Publish_rejects_invalid_arguments()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IWebhookPublisher>();

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(" ", new { id = 1 }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PublishAsync("order.created", null!));
    }

    [Fact]
    public async Task Replay_unknown_delivery_returns_false()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IWebhookPublisher>();

        Assert.False(await publisher.ReplayAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Replay_removed_subscription_returns_false()
    {
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IWebhookPublisher>();
        var subscriptions = provider.GetRequiredService<IWebhookSubscriptionStore>();
        var deliveries = provider.GetRequiredService<IWebhookDeliveryStore>();

        var subscription = new WebhookSubscription
        {
            Url = "https://receiver.test/hooks",
            EventTypes = ["*"],
        };
        subscriptions.Add(subscription);
        await publisher.PublishAsync("order.created", new { id = 1 });

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (deliveries.Recent().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var delivery = Assert.Single(deliveries.Recent());
        subscriptions.Remove(subscription.Id);

        Assert.False(await publisher.ReplayAsync(delivery.Id));
    }

    [Fact]
    public void Subscription_matching_honors_wildcard_and_disabled_flag()
    {
        var all = new WebhookSubscription { Url = "https://receiver.test", EventTypes = ["*"] };
        var typed = new WebhookSubscription { Url = "https://receiver.test", EventTypes = ["order.created"] };
        var disabled = new WebhookSubscription { Url = "https://receiver.test", Enabled = false };

        Assert.True(all.Matches("anything"));
        Assert.True(typed.Matches("ORDER.CREATED"));
        Assert.False(typed.Matches("user.created"));
        Assert.False(disabled.Matches("order.created"));
    }

    [Fact]
    public void Delivery_store_trims_to_capacity()
    {
        var store = new InMemoryWebhookDeliveryStore(capacity: 2);

        for (var index = 0; index < 3; index++)
        {
            store.Add(new WebhookDelivery
            {
                SubscriptionId = Guid.NewGuid(),
                EventType = "e",
                Url = "https://receiver.test",
                Payload = "{}",
            });
        }

        Assert.Equal(2, store.Recent().Count);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonWebhooks(options =>
        {
            options.HttpMessageHandler = new OkHandler();
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        });

        var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        return provider;
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
