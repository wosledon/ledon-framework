# 缓存

```csharp
builder.Services.AddLedonCaching();        // 进程内：同 key 并发未命中 single-flight
// 或多实例共享：
builder.Services.AddLedonDistributedCaching(o => o.RedisConnectionString = "localhost:6379");
```

## 用法

```csharp
public class CatalogService(ICacheService cache)
{
    public async Task<Product?> GetAsync(int id, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync($"product:{id}", async token =>
        {
            var product = await db.Products.FindAsync([id], token);
            return product;
        }, expiration: TimeSpan.FromMinutes(10), cancellationToken: ct);
}
```

同一 key 的并发未命中会共享同一次回源（single-flight）——缓存击穿时数据库只被压一次。某个调用方取消只影响它自己，不会杀掉共享回源。

## 负缓存（防穿透）

```csharp
builder.Services.AddLedonCaching(o =>
{
    o.NegativeExpiration = TimeSpan.FromSeconds(30);   // TimeSpan.Zero 关闭
});
```

回源返回 `null` 时按短 TTL 缓存一个"未命中"哨兵，让同一 key 的后续请求直接返回 null
而不再打到底层存储。热点 key 过期 + 慢回源时，没有负缓存会让所有并发请求同时穿透到 DB。

代价：写入后最长 `NegativeExpiration` 内新 key 仍读到 null——只对"读多写少、
允许秒级延迟"的 key 开启。回源拿到 null 时**不会**把 null 当正常值缓存
（否则后续查询全部"命中 null"再也不回源）。

## 两种实现，一个契约

| 注册方式                    | 实现                       | 适用场景                       |
| --------------------------- | -------------------------- | ------------------------------ |
| `AddLedonCaching()`         | 进程内内存                 | 单实例、热点数据               |
| `AddLedonDistributedCaching()` | `IDistributedCache`（Redis） | 多实例部署，缓存需跨进程共享 |
| `AddLedonHybridCaching()`   | L1 内存 + L2 分布式        | 读多写少的热数据（需先注册 `IDistributedCache`） |

分布式实现不配 Redis 连接字符串时退化为进程内内存缓存。换实现不改业务代码（契约与实现分离）。

## 契约扩展：异步 / 滑动过期 / 前缀删除

`ICacheService` 在原有 API 之上补齐了常用能力（原签名全部保留，向后兼容）：

```csharp
await cache.SetAsync("product:1", product, expiration: TimeSpan.FromMinutes(10), ct);
await cache.RemoveAsync("product:1", ct);

cache.RemoveByPrefix("product:");              // 前缀删除（同步）
await cache.RemoveByPrefixAsync("product:", ct);

// 滑动过期：一段时间未访问即失效（与绝对过期可同时使用）
cache.Set("session:abc", state, TimeSpan.FromHours(1), slidingExpiration: TimeSpan.FromMinutes(20));
```

- 内存实现：前缀删除基于进程内 key 索引（随逐出/移除同步清理，不泄漏）；
- 分布式实现：Redis 场景经 SCAN 模式匹配全局删除（`AddLedonDistributedCaching` 自动注册 multiplexer）；
- 默认滑动过期可用 `DefaultSlidingExpiration` 配置。

## 混合两级缓存（L1 内存 + L2 分布式）

读多写少的热数据用 .NET 10 HybridCache：L1 命中零网络往返，跨实例一致性由 L2 保证：

```csharp
builder.Services.AddLedonDistributedCaching(o => o.RedisConnectionString = "localhost:6379");
builder.Services.AddLedonHybridCaching();   // 注册了 IDistributedCache 即自动两级
```

- 同一个 `ICacheService` 契约，业务代码零改动；
- `GetAsync` 未命中**不写缓存**（不会把 null 缓存成值）；
- 前缀删除按键的命名空间（首段）打标签：`users:1` 的标签是 `users`，`RemoveByPrefix("users")` 经 L1+L2 全局生效——key 请按 `命名空间:键` 约定书写；
- 滑动过期在两级缓存下无意义（L2 无法表达"未访问"），忽略。

## 实例隔离

`KeyPrefix` 让同一进程内的多个实例（如测试并行）互不串键：

```csharp
services.AddLedonCaching(o => o.KeyPrefix = "module-a:");
```

## 多租户：缓存键隔离（防跨租户数据泄漏）

`KeyPrefix` 是进程级常量——多租户应用共享缓存时两个租户同一 key 会互读。配了多租户包后自动按租户加前缀：

```csharp
builder.Services.AddLedonMultiTenancy();   // 默认 CacheKeyPrefix = "tenant:"
builder.Services.AddLedonCaching();        // 缓存键变成 tenant:{租户ID}:{原key}
```

- 取值走环境租户（`TenantContext.AmbientTenantId`，Web 中间件/后台 SetAmbient 写入），单例安全；
- 无租户上下文（单租户模式）自动退化为静态前缀，不受影响；
- 关闭：`AddLedonMultiTenancy(o => o.CacheKeyPrefix = "")`，改为自行在 key 里带租户；
- 自定义前缀逻辑：实现 `ICacheKeyPrefixProvider` 注册即可（TryAdd，手动优先）。

## 选择建议
- 读多写少、允许秒级陈旧 → 缓存
- 强一致要求 → 直接读库，缓存只用于无关紧要的投影
- 大 value（>100KB）谨慎：Redis 单 value 默认上限 512MB，但大 value 会拖慢序列化
