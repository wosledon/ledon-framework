using System.Threading.Channels;
using Ledon.Web;
using Ledon.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 出站 Webhook 入口：
/// <code>
/// builder.Services.AddLedonWebhooks(o =&gt;
/// {
///     o.MaxAttempts = 5;
///     o.RequireAuthorization = true;
/// });
/// </code>
/// 投递走后台队列 + 指数退避；管理端点随 <c>UseLedonWeb()</c> 自动映射。
/// </summary>
public static class LedonWebhookServiceCollectionExtensions
{
    public static IServiceCollection AddLedonWebhooks(
        this IServiceCollection services,
        Action<LedonWebhookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonWebhookOptions();
        configure?.Invoke(options);

        if (options.MaxAttempts < 1)
            throw new InvalidOperationException("LedonWebhookOptions.MaxAttempts 必须至少为 1");

        if (options.QueueCapacity < 1)
            throw new InvalidOperationException("LedonWebhookOptions.QueueCapacity 必须大于 0");

        if (options.RequestTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("LedonWebhookOptions.RequestTimeout 必须大于 0");

        if (options.RetryBaseDelay < TimeSpan.Zero)
            throw new InvalidOperationException("LedonWebhookOptions.RetryBaseDelay 不能为负数");

        services.TryAddSingleton(options);
        services.TryAddSingleton<IWebhookSubscriptionStore, InMemoryWebhookSubscriptionStore>();
        services.TryAddSingleton<IWebhookDeliveryStore>(
            _ => new InMemoryWebhookDeliveryStore(options.DeliveryHistorySize));

        services.TryAddSingleton(_ => Channel.CreateBounded<WebhookJob>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        }));

        services.TryAddSingleton<IWebhookPublisher, WebhookPublisher>();
        services.AddHostedService<WebhookDeliveryWorker>();

        // 框架包自身不进约定扫描；管理端点显式注册
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, WebhookEndpoints>());

        return services;
    }
}

/// <summary>Webhook 管理端点（订阅 CRUD、投递记录、重放）。</summary>
public sealed class WebhookEndpoints(
    IWebhookSubscriptionStore subscriptions,
    IWebhookDeliveryStore deliveries,
    IWebhookPublisher publisher,
    LedonWebhookOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/webhooks");

        if (options.RequireAuthorization)
            group.RequireAuthorization();

        group.MapGet("/subscriptions", () =>
            Results.Ok(subscriptions.GetAll().Select(ToDto)));

        group.MapPost("/subscriptions", (CreateWebhookSubscriptionRequest request) =>
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new { error = "Url 必须是 http/https 绝对地址" });
            }

            if (request.EventTypes is { Count: > 0 } && request.EventTypes.Any(string.IsNullOrWhiteSpace))
                return Results.BadRequest(new { error = "EventTypes 不能包含空值" });

            var subscription = new WebhookSubscription
            {
                Url = request.Url,
                Secret = request.Secret,
                EventTypes = request.EventTypes ?? [],
                TenantId = request.TenantId,
            };

            subscriptions.Add(subscription);

            return Results.Created($"/webhooks/subscriptions/{subscription.Id}", ToDto(subscription));
        });

        group.MapDelete("/subscriptions/{id:guid}", (Guid id) =>
            subscriptions.Remove(id) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/subscriptions/{id:guid}/enable", (Guid id) =>
            subscriptions.SetEnabled(id, true) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/subscriptions/{id:guid}/disable", (Guid id) =>
            subscriptions.SetEnabled(id, false) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/deliveries", (int? count) =>
            Results.Ok(deliveries.Recent(count ?? 100).Select(ToDto)));

        group.MapGet("/deliveries/dead-letters", (int? count) =>
            Results.Ok(deliveries.DeadLetters(count ?? 100).Select(ToDto)));

        group.MapPost("/deliveries/{id:guid}/replay", async (Guid id, CancellationToken ct) =>
            await publisher.ReplayAsync(id, ct).ConfigureAwait(false)
                ? Results.Accepted()
                : Results.NotFound());
    }

    private static object ToDto(WebhookSubscription subscription) => new
    {
        subscription.Id,
        subscription.Url,
        subscription.EventTypes,
        subscription.Enabled,
        subscription.TenantId,
        subscription.CreatedAtUtc,
        HasSecret = !string.IsNullOrEmpty(subscription.Secret),
    };

    private static object ToDto(WebhookDelivery delivery) => new
    {
        delivery.Id,
        delivery.SubscriptionId,
        delivery.EventType,
        delivery.Url,
        delivery.Attempts,
        delivery.Success,
        delivery.StatusCode,
        delivery.Error,
        delivery.CreatedAtUtc,
        delivery.CompletedAtUtc,
    };
}

/// <summary>创建订阅请求。</summary>
public sealed record CreateWebhookSubscriptionRequest(
    string Url,
    string? Secret = null,
    IReadOnlyCollection<string>? EventTypes = null,
    string? TenantId = null);
