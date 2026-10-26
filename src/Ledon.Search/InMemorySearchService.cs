using System.Collections.Concurrent;
using System.Text.Json;

namespace Ledon.Search;

/// <summary>进程内搜索：JSON 序列化后做大小写不敏感的子串匹配（开发/测试用）。</summary>
public sealed class InMemorySearchService : ISearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _indexes =
        new(StringComparer.OrdinalIgnoreCase);

    public Task IndexAsync<T>(
        string index,
        string id,
        T document,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var json = JsonSerializer.Serialize(document, JsonOptions);
        var entries = _indexes.GetOrAdd(index, _ => new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal));
        entries[id] = new Entry(id, json, tenantId);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string index, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (_indexes.TryGetValue(index, out var entries))
            entries.TryRemove(id, out _);

        return Task.CompletedTask;
    }

    public Task<SearchResult<T>> SearchAsync<T>(
        string index,
        string? query = null,
        int page = 1,
        int pageSize = 20,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);

        IEnumerable<Entry> entries = _indexes.TryGetValue(index, out var found)
            ? found.Values
            : [];

        if (!string.IsNullOrEmpty(tenantId))
            entries = entries.Where(entry => string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(query))
        {
            entries = entries.Where(entry =>
                entry.Json.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var matched = entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        var items = matched
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(entry => JsonSerializer.Deserialize<T>(entry.Json, JsonOptions)!)
            .ToArray();

        return Task.FromResult(new SearchResult<T>(items, matched.LongLength, page, pageSize));
    }

    private sealed record Entry(string Id, string Json, string? TenantId);
}
