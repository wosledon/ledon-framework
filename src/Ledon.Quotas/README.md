# Ledon.Quotas

固定窗口配额/限流：进程内或多实例共享（`IDistributedCache`），支持按租户/API Key/用户自定义配额键。

```csharp
builder.Services.AddLedonQuotas(o =>
{
    o.DefaultLimit = 100;
    o.Window = TimeSpan.FromMinutes(1);
    o.UseDistributedCache = true;   // 需先注册 Redis/分布式缓存
    o.KeySelector = context => context.User.FindFirst("tenant")?.Value ?? "anonymous";
});

app.UseLedonQuotas();   // 在路由/端点之前
```

- 返回头：`X-Quota-Limit` / `X-Quota-Remaining`；超限返回 429 + `Retry-After`；
- 默认按客户端 IP 计数，可按业务改成租户/API Key/用户维度；
- 分布式实现基于 `IDistributedCache` 的读改写，非原子；强一致限流请换 Redis `INCR`/Lua 实现 `IQuotaStore`；
- `IQuotaStore` 可替换为 Redis/数据库实现。

完整文档：<https://github.com/wosledon/ledon-framework>
