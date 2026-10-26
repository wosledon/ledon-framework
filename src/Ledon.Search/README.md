# Ledon.Search

搜索抽象：进程内搜索（开发/测试）与 Meilisearch 两种实现，支持按租户过滤。

```csharp
builder.Services.AddLedonSearch(o =>
{
    o.Provider = SearchProvider.Meilisearch;
    o.Endpoint = "http://localhost:7700";
    o.ApiKey = "...";
});

public class ProductSearch(ISearchService search)
{
    public Task IndexAsync(Product product, CancellationToken ct) =>
        search.IndexAsync("products", product.Id.ToString(), product, tenantId: product.TenantId, ct);

    public Task<SearchResult<Product>> FindAsync(string q, CancellationToken ct) =>
        search.SearchAsync<Product>("products", q, page: 1, pageSize: 20, ct: ct);
}
```

- `ISearchService`：`IndexAsync` / `DeleteAsync` / `SearchAsync`；
- 租户过滤：索引时写 `tenantId`，搜索时传 `tenantId`；
- 内存实现按 JSON 子串匹配，重启丢失；Meilisearch 走 HTTP API；
- 索引名/文档 ID 会做 URL 转义。

完整文档：<https://github.com/wosledon/ledon-framework>
