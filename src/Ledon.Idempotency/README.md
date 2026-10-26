# Ledon.Idempotency

增强版幂等：`Idempotency-Key` + 请求指纹 + 响应重放 + 同键冲突 + 多实例锁。

```csharp
builder.Services.AddLedonIdempotency(o =>
{
    o.Ttl = TimeSpan.FromHours(24);
    o.MaxRequestBodyBytes = 1024 * 1024;
});

app.UseRouting();
app.UseLedonIdempotency();     // 路由之后、端点之前
```

客户端：

```http
POST /api/orders
Idempotency-Key: 3f1c...
Content-Type: application/json
```

- 同键 + 同请求指纹：重放首次 2xx 响应，带 `Idempotency-Replayed: true`；
- 同键 + 不同请求体/查询串：返回 409 + `Idempotency-Conflict: true`；
- 同键并发：进程内单飞；注册 `IDistributedLock`（Redis）后支持多实例去重；
- 默认使用内存存储；注册 `IDistributedCache` 后自动切换为分布式响应缓存；
- 默认缓存 2xx，失败不占用键；请求/响应体可配置上限；
- 默认按 `X-Tenant-Id` + 用户 ID 分区，避免跨租户/用户撞键。

> 已开启 `LedonWebOptions.UseIdempotency` 时请关闭它，避免两个幂等中间件重复处理。

完整文档：<https://github.com/wosledon/ledon-framework>
