# Ledon.Resilience

Ledon Framework 的弹性策略：HTTP 韧性管道（重试 / 超时 / 熔断），默认接管全部 HttpClient。

```csharp
builder.Services.AddLedonResilience();   // 经 ConfigureHttpClientDefaults 对全部客户端生效
```

默认值即为生产可用配置：

| 策略     | 默认行为                                             |
| -------- | ---------------------------------------------------- |
| 重试     | 5xx / 429 / 网络故障，指数退避 + 抖动，最多 3 次      |
| 尝试超时 | 单次尝试 30 秒                                       |
| 熔断     | 最小采样 5 次中失败比例 ≥ 50% 时打开，持续 30 秒      |

```csharp
builder.Services.AddLedonResilience(o =>
{
    o.MaxRetryAttempts = 5;
    o.BaseDelay = TimeSpan.FromSeconds(1);
    o.AttemptTimeout = TimeSpan.FromSeconds(10);
    o.BreakDuration = TimeSpan.FromSeconds(15);
});
```

覆盖出站 HTTP（`HttpClient`）；数据库/Redis 等依赖的韧性由各自驱动负责。

完整文档：<https://github.com/wosledon/ledon-framework>
