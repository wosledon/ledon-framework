using System.Collections.Concurrent;

namespace Ledon.Webhooks;

/// <summary>Webhook 订阅：匹配事件类型后把事件 POST 到目标地址。</summary>
public sealed record WebhookSubscription
{
    /// <summary>订阅标识。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>目标地址（仅接受 http/https）。</summary>
    public required string Url { get; init; }

    /// <summary>HMAC 签名密钥；为空则不签名（不推荐）。</summary>
    public string? Secret { get; init; }

    /// <summary>关心的事件类型；为空表示全部。</summary>
    public IReadOnlyCollection<string> EventTypes { get; init; } = [];

    /// <summary>是否启用。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>可选租户标识（仅作元数据，便于按租户管理）。</summary>
    public string? TenantId { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>是否匹配指定事件类型（支持 <c>*</c> 通配）。</summary>
    public bool Matches(string eventType) =>
        Enabled && (EventTypes.Count == 0
            || EventTypes.Contains("*")
            || EventTypes.Contains(eventType, StringComparer.OrdinalIgnoreCase));
}

/// <summary>一次 Webhook 投递记录。</summary>
public sealed record WebhookDelivery
{
    /// <summary>投递标识（也是接收方幂等键）。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>订阅标识。</summary>
    public required Guid SubscriptionId { get; init; }

    /// <summary>事件类型。</summary>
    public required string EventType { get; init; }

    /// <summary>目标地址。</summary>
    public required string Url { get; init; }

    /// <summary>请求体（重放用）。</summary>
    public required string Payload { get; init; }

    /// <summary>尝试次数。</summary>
    public int Attempts { get; init; }

    /// <summary>是否最终成功。</summary>
    public bool Success { get; init; }

    /// <summary>最后一次 HTTP 状态码。</summary>
    public int? StatusCode { get; init; }

    /// <summary>最后一次错误。</summary>
    public string? Error { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>完成时间（UTC）。</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }
}

/// <summary>订阅存储（默认内存；可替换为数据库/配置中心实现）。</summary>
public interface IWebhookSubscriptionStore
{
    IReadOnlyList<WebhookSubscription> GetAll();

    WebhookSubscription? Find(Guid id);

    void Add(WebhookSubscription subscription);

    bool Remove(Guid id);

    bool SetEnabled(Guid id, bool enabled);
}

/// <summary>投递记录存储（默认内存环形缓冲）。</summary>
public interface IWebhookDeliveryStore
{
    void Add(WebhookDelivery delivery);

    IReadOnlyList<WebhookDelivery> Recent(int count = 100);

    IReadOnlyList<WebhookDelivery> DeadLetters(int count = 100);

    WebhookDelivery? Find(Guid id);
}

/// <summary>默认内存订阅存储。</summary>
public sealed class InMemoryWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly ConcurrentDictionary<Guid, WebhookSubscription> _subscriptions = new();

    public IReadOnlyList<WebhookSubscription> GetAll() =>
        _subscriptions.Values.OrderBy(subscription => subscription.CreatedAtUtc).ToArray();

    public WebhookSubscription? Find(Guid id) =>
        _subscriptions.TryGetValue(id, out var subscription) ? subscription : null;

    public void Add(WebhookSubscription subscription) => _subscriptions[subscription.Id] = subscription;

    public bool Remove(Guid id) => _subscriptions.TryRemove(id, out _);

    public bool SetEnabled(Guid id, bool enabled)
    {
        if (!_subscriptions.TryGetValue(id, out var subscription))
            return false;

        _subscriptions[id] = subscription with { Enabled = enabled };
        return true;
    }
}

/// <summary>默认内存投递记录（有界，避免长期运行无限增长）。</summary>
public sealed class InMemoryWebhookDeliveryStore(int capacity) : IWebhookDeliveryStore
{
    private readonly Lock _gate = new();
    private readonly List<WebhookDelivery> _deliveries = [];

    public void Add(WebhookDelivery delivery)
    {
        lock (_gate)
        {
            _deliveries.Add(delivery);

            if (_deliveries.Count > capacity)
                _deliveries.RemoveRange(0, _deliveries.Count - capacity);
        }
    }

    public IReadOnlyList<WebhookDelivery> Recent(int count = 100)
    {
        lock (_gate)
        {
            return _deliveries
                .OrderByDescending(delivery => delivery.CreatedAtUtc)
                .Take(Math.Max(1, count))
                .ToArray();
        }
    }

    public IReadOnlyList<WebhookDelivery> DeadLetters(int count = 100)
    {
        lock (_gate)
        {
            return _deliveries
                .Where(delivery => !delivery.Success)
                .OrderByDescending(delivery => delivery.CreatedAtUtc)
                .Take(Math.Max(1, count))
                .ToArray();
        }
    }

    public WebhookDelivery? Find(Guid id)
    {
        lock (_gate)
        {
            return _deliveries.LastOrDefault(delivery => delivery.Id == id);
        }
    }
}
