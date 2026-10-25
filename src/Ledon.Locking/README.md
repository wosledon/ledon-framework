# Ledon.Locking

跨进程互斥锁：防重、资源独占、定时任务防重入。进程内 / Redis 两种实现，业务只依赖 `IDistributedLock`。

```csharp
builder.Services.AddLedonLocking();   // 进程内（单实例）

public class OrderService(IDistributedLock locker)
{
    public async Task PayAsync(string orderId, CancellationToken ct = default)
    {
        await using var handle = await locker.AcquireAsync($"order:{orderId}", ct: ct);
        // 同一订单的并发支付被串行化
    }
}
```

多实例部署换 Redis（互斥跨进程）：

```csharp
builder.Services.AddLedonLocking(
    o => o.Provider = LedonLockProvider.Redis,
    o => o.RedisConnectionString = "localhost:6379");
```

- `TryAcquireAsync` 不等待，拿不到返回 `null`；`AcquireAsync` 等待超时抛 `TimeoutException`；
- 持锁默认 30 秒到期自动释放（持锁进程崩溃不死锁），可传 `expiry` 覆盖；
- Redis 实现为 SET NX PX + Lua 校验令牌释放，不会误删他人已到期的锁；
- `await using` / 显式 `ReleaseAsync` 均幂等。

完整文档：<https://github.com/wosledon/ledon-framework>
