# Ledon.Search.Sync

搜索索引增量同步：队列 + 退避重试 + 后台 Worker，业务保存实体后入队即可，避免“写库成功、索引失败”的静默不一致。

```csharp
builder.Services.AddLedonSearch(o => o.Provider = SearchProvider.Meilisearch);
builder.Services.AddLedonSearchSync(o =>
{
    o.AutoStart = true;
    o.MaxRetries = 5;
    o.BatchSize = 32;
});
```

```csharp
public class ProductService(SearchSyncService sync)
{
    public Task SaveAsync(Product product, CancellationToken ct) =>
        sync.EnqueueUpsertAsync("products", product.Id.ToString(), product, product.TenantId, ct);

    public Task DeleteAsync(Product product, CancellationToken ct) =>
        sync.EnqueueDeleteAsync("products", product.Id.ToString(), product.TenantId, ct);
}
```

- `ISearchSyncQueue` 默认内存实现；生产可替换为数据库发件箱/持久队列，保证进程重启不丢；
- 失败按指数退避（`RetryDelay` 起，`MaxRetryDelay` 封顶），超过 `MaxRetries` 丢弃并记录；
- `AutoStart` 注册 `BackgroundService` 轮询；`ProcessOnceAsync` 可在测试/定时任务中手动触发；
- Upsert 的 `Document` 是对象，搜索实现负责序列化；删除只带索引与 ID；
- 多租户场景传 `tenantId`，`ISearchService` 会按租户写入/过滤。

完整文档：<https://github.com/wosledon/ledon-framework>
