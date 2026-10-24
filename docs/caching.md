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

## 两种实现，一个契约

| 注册方式                    | 实现                       | 适用场景                       |
| --------------------------- | -------------------------- | ------------------------------ |
| `AddLedonCaching()`         | 进程内内存                 | 单实例、热点数据               |
| `AddLedonDistributedCaching()` | `IDistributedCache`（Redis） | 多实例部署，缓存需跨进程共享 |

分布式实现不配 Redis 连接字符串时退化为进程内内存缓存。换实现不改业务代码（契约与实现分离）。

## 实例隔离

`KeyPrefix` 让同一进程内的多个实例（如测试并行）互不串键：

```csharp
services.AddLedonCaching(o => o.KeyPrefix = "module-a:");
```

## 选择建议

- 读多写少、允许秒级陈旧 → 缓存
- 强一致要求 → 直接读库，缓存只用于无关紧要的投影
- 大 value（>100KB）谨慎：Redis 单 value 默认上限 512MB，但大 value 会拖慢序列化
