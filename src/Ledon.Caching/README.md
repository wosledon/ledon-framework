# Ledon.Caching

Ledon Framework 的缓存：进程内（single-flight）/ 分布式（Redis）两种实现，同一个 `ICacheService` 契约。

```csharp
builder.Services.AddLedonCaching();                          // 进程内：并发未命中只回源一次
builder.Services.AddLedonDistributedCaching(o =>             // 多实例共享
    o.RedisConnectionString = "localhost:6379");
```

```csharp
public class CatalogService(ICacheService cache)
{
    public Task<Product?> GetAsync(int id, CancellationToken ct = default) =>
        cache.GetOrCreateAsync($"product:{id}", async token =>
        {
            var product = await db.Products.FindAsync([id], token);
            return product;
        }, expiration: TimeSpan.FromMinutes(10), cancellationToken: ct);
}
```

同一 key 的并发未命中共享同一次回源（缓存击穿时数据库只被压一次）；某个调用方取消只影响它自己。`KeyPrefix` 隔离同进程内的多个实例。换实现不改业务代码。

完整文档：<https://github.com/wosledon/ledon-framework>
