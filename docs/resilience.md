# 弹性策略

```csharp
builder.Services.AddLedonResilience();   // 接管全部 HttpClient（默认）
```

基于 Polly 的 HTTP 韧性管道，经 `ConfigureHttpClientDefaults` 默认对**全部** `IHttpClientFactory` 客户端生效——命名客户端、类型化客户端都无需逐个配置。

## 默认策略

| 策略     | 默认行为                                                     |
| -------- | ------------------------------------------------------------ |
| 重试     | 5xx / 429 / 网络故障，指数退避 + 抖动，最多 3 次              |
| 尝试超时 | 单次尝试 30 秒                                               |
| 熔断     | 最小采样 5 次中失败比例 ≥ 50% 时打开，持续 30 秒              |

```csharp
builder.Services.AddLedonResilience(o =>
{
    o.MaxRetryAttempts = 5;
    o.BaseDelay = TimeSpan.FromSeconds(1);
    o.AttemptTimeout = TimeSpan.FromSeconds(10);
    o.BreakDuration = TimeSpan.FromSeconds(15);
});
```

`MaxRetryAttempts <= 0` 时跳过重试策略（只留超时与熔断）。

## 适用范围

默认 `ApplyToAllHttpClients = true`。只想对特定客户端生效时置 false，自行用 `AddHttpClient(...).AddStandardResilienceHandler()` 或框架的重试处理器配置。

## 边界

策略只覆盖**出站 HTTP**（`HttpClient`）。数据库、Redis 等依赖的韧性由各自驱动的连接池/重试机制负责，不在此管道内。重试对**非幂等**请求（POST 创建）默认同样生效——下游不保证幂等时，把写操作改造成幂等（幂等键）或对该客户端关闭重试。
