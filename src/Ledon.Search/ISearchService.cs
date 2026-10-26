namespace Ledon.Search;

/// <summary>搜索结果。</summary>
public sealed record SearchResult<T>(
    IReadOnlyList<T> Items,
    long Total,
    int Page,
    int PageSize);

/// <summary>搜索服务：索引、删除、查询（带可选租户过滤）。</summary>
public interface ISearchService
{
    /// <summary>写入/更新文档（按 <paramref name="id"/> 覆盖）。</summary>
    Task IndexAsync<T>(
        string index,
        string id,
        T document,
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除文档。</summary>
    Task DeleteAsync(string index, string id, CancellationToken cancellationToken = default);

    /// <summary>搜索文档（空查询返回全部；按租户过滤）。</summary>
    Task<SearchResult<T>> SearchAsync<T>(
        string index,
        string? query = null,
        int page = 1,
        int pageSize = 20,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>搜索选项。</summary>
public sealed class LedonSearchOptions
{
    /// <summary>搜索引擎类型，默认内存（开发/测试）。</summary>
    public SearchProvider Provider { get; set; } = SearchProvider.InMemory;

    /// <summary>Meilisearch 地址（Provider = Meilisearch 必填），如 <c>http://localhost:7700</c>。</summary>
    public string? Endpoint { get; set; }

    /// <summary>Meilisearch API Key（可选）。</summary>
    public string? ApiKey { get; set; }

    /// <summary>HTTP 超时，默认 15 秒。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>自定义 HTTP 消息处理器（测试注入）。</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}

/// <summary>搜索引擎类型。</summary>
public enum SearchProvider
{
    /// <summary>进程内搜索（开发/测试；重启丢失）。</summary>
    InMemory = 0,

    /// <summary>Meilisearch HTTP API。</summary>
    Meilisearch = 1,
}
