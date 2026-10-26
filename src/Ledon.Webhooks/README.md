# Ledon.Webhooks

出站 Webhook：事件订阅、HMAC 签名、指数退避重试、死信记录与重放。投递走后台队列，不阻塞业务请求。

```csharp
builder.Services.AddLedonWebhooks(o =>
{
    o.MaxAttempts = 5;
    o.RequireAuthorization = true;   // 生产建议开启管理端点授权
});
```

发布事件：

```csharp
public class OrderService(IWebhookPublisher webhooks)
{
    public Task CreatedAsync(Guid orderId, CancellationToken ct) =>
        webhooks.PublishAsync("order.created", new { orderId }, ct);
}
```

接收方验签：

```csharp
var timestamp = request.Headers["X-Ledon-Timestamp"];
var body = await new StreamReader(request.Body).ReadToEndAsync();
var expected = HMACSHA256(secret, $"{timestamp}.{body}");
// 与 X-Ledon-Signature: sha256=... 比较
```

- 事件信封：`{ id, type, occurredAtUtc, data }`；
- 请求头：`X-Ledon-Event` / `X-Ledon-Delivery` / `X-Ledon-Timestamp` / `X-Ledon-Signature`；
- 订阅 `EventTypes` 为空匹配全部，支持 `*` 通配；
- 管理端点：`/webhooks/subscriptions`、`/webhooks/deliveries`、死信查询与重放；
- 投递记录默认内存环形缓冲，可替换 `IWebhookDeliveryStore` 持久化。

完整文档：<https://github.com/wosledon/ledon-framework>
