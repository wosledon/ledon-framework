using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Webhooks;

/// <summary>出站 Webhook 发布入口。</summary>
public interface IWebhookPublisher
{
    /// <summary>发布事件到所有匹配且启用的订阅；返回入队投递数。</summary>
    Task<int> PublishAsync(string eventType, object payload, CancellationToken cancellationToken = default);

    /// <summary>重放一条历史投递（按原请求体重发）。</summary>
    Task<bool> ReplayAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}

internal sealed record WebhookJob(
    Guid DeliveryId,
    WebhookSubscription Subscription,
    string EventType,
    string Body,
    DateTimeOffset OccurredAtUtc);

/// <summary>把事件写成标准信封并按订阅入队；实际 HTTP 投递由后台 worker 完成。</summary>
internal sealed class WebhookPublisher(
    Channel<WebhookJob> queue,
    IWebhookSubscriptionStore subscriptions,
    IWebhookDeliveryStore deliveries) : IWebhookPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> PublishAsync(string eventType, object payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        var occurredAtUtc = DateTimeOffset.UtcNow;
        var data = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var queued = 0;

        foreach (var subscription in subscriptions.GetAll())
        {
            if (!subscription.Matches(eventType))
                continue;

            var deliveryId = Guid.NewGuid();
            var body = JsonSerializer.Serialize(new
            {
                id = deliveryId,
                type = eventType,
                occurredAtUtc,
                data,
            }, JsonOptions);

            await queue.Writer.WriteAsync(
                new WebhookJob(deliveryId, subscription, eventType, body, occurredAtUtc),
                cancellationToken).ConfigureAwait(false);

            queued++;
        }

        return queued;
    }

    public async Task<bool> ReplayAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = deliveries.Find(deliveryId);

        if (delivery is null)
            return false;

        var subscription = subscriptions.Find(delivery.SubscriptionId);

        if (subscription is null)
            return false;

        await queue.Writer.WriteAsync(
            new WebhookJob(
                Guid.NewGuid(),   // 重放使用新投递 ID，接收方仍可按 event id 去重
                subscription,
                delivery.EventType,
                delivery.Payload,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>后台投递 worker：指数退避重试 + HMAC 签名 + 投递记录。</summary>
internal sealed class WebhookDeliveryWorker(
    Channel<WebhookJob> queue,
    IWebhookDeliveryStore deliveries,
    LedonWebhookOptions options,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    private readonly HttpClient _http = new(
        options.HttpMessageHandler ?? new SocketsHttpHandler(),
        disposeHandler: options.HttpMessageHandler is null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            await DeliverAsync(job, stoppingToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private async Task DeliverAsync(WebhookJob job, CancellationToken stoppingToken)
    {
        var maxAttempts = Math.Max(1, options.MaxAttempts);
        var attempt = 0;
        int? statusCode = null;
        string? error = null;

        while (attempt < maxAttempts && !stoppingToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                using var request = CreateRequest(job);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(options.RequestTimeout);

                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    Record(job, attempt, success: true, statusCode, error: null);
                    return;
                }

                error = $"HTTP {statusCode}";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                error = "请求超时";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                error = ex.Message;
            }

            if (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromTicks(options.RetryBaseDelay.Ticks * (1L << (attempt - 1)));

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        Record(job, attempt, success: false, statusCode, error);

        logger.LogWarning(
            "Webhook 投递失败：{Event} → {Url}（{Attempts} 次，{Error}）",
            job.EventType, job.Subscription.Url, attempt, error);
    }

    private HttpRequestMessage CreateRequest(WebhookJob job)
    {
        var content = new StringContent(job.Body, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, job.Subscription.Url)
        {
            Content = content,
        };

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        request.Headers.TryAddWithoutValidation("X-Ledon-Event", job.EventType);
        request.Headers.TryAddWithoutValidation("X-Ledon-Delivery", job.DeliveryId.ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Ledon-Timestamp", timestamp);

        if (!string.IsNullOrEmpty(job.Subscription.Secret))
        {
            var signature = ComputeSignature(job.Subscription.Secret, timestamp, job.Body);
            request.Headers.TryAddWithoutValidation("X-Ledon-Signature", $"sha256={signature}");
        }

        return request;
    }

    private void Record(WebhookJob job, int attempts, bool success, int? statusCode, string? error) =>
        deliveries.Add(new WebhookDelivery
        {
            Id = job.DeliveryId,
            SubscriptionId = job.Subscription.Id,
            EventType = job.EventType,
            Url = job.Subscription.Url,
            Payload = job.Body,
            Attempts = attempts,
            Success = success,
            StatusCode = statusCode,
            Error = error,
            CreatedAtUtc = job.OccurredAtUtc,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });

    /// <summary>接收方按同一算法验签：HMAC-SHA256(secret, "{timestamp}.{body}")。</summary>
    public static string ComputeSignature(string secret, string timestamp, string body)
    {
        var data = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
