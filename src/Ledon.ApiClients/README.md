# Ledon.ApiClients

强类型/命名出站 HTTP 客户端：OAuth2 client_credentials 自动取令牌并缓存刷新，自动传播关联 ID、traceparent。

```csharp
builder.Services.AddLedonApiClient("orders", o =>
{
    o.BaseAddress = "https://orders.internal/";
    o.ClientId = configuration["Orders:ClientId"];
    o.ClientSecret = configuration["Orders:ClientSecret"];
    o.TokenEndpoint = "https://identity/connect/token";
    o.Scope = "orders.read orders.write";
});
```

```csharp
public class OrderGateway(IHttpClientFactory factory)
{
    public Task<HttpResponseMessage> GetAsync(CancellationToken ct) =>
        factory.CreateClient("orders").GetAsync("orders/1", ct);
}
```

类型化客户端：

```csharp
builder.Services.AddLedonApiClient<OrdersClient>("orders");
public sealed class OrdersClient(HttpClient http) { /* 自定义强类型方法 */ }
```

- 令牌缓存到过期前 `TokenRefreshSkew`（默认 1 分钟）；`Invalidate()` 可强制刷新；
- 每个请求自动附加 `Authorization: Bearer`（未配置凭据则跳过）；
- `X-Correlation-ID` 优先复用入站请求头，否则用 `HttpContext.TraceIdentifier`/GUID；
- `traceparent` 来自当前 `Activity`（未手动设置时）；
- 默认请求头、超时、BaseAddress 均在注册时配置；
- 令牌缓存为进程内缓存，多实例各自获取令牌；如需共享可替换 `IClientCredentialsTokenProvider`。

完整文档：<https://github.com/wosledon/ledon-framework>
