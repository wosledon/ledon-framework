using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ledon.Search;

/// <summary>Meilisearch 搜索实现：索引写入、删除与搜索（支持租户 filter）。</summary>
public sealed class MeilisearchSearchService : ISearchService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LedonSearchOptions _options;
    private readonly HttpClient _http;

    public MeilisearchSearchService(LedonSearchOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient
        {
            Timeout = options.Timeout,
        };

        if (!string.IsNullOrEmpty(options.Endpoint))
            _http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");

        if (!string.IsNullOrEmpty(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    public async Task IndexAsync<T>(
        string index,
        string id,
        T document,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var node = JsonSerializer.SerializeToNode(document, JsonOptions) as JsonObject ?? new JsonObject();
        node["id"] = id;

        if (!string.IsNullOrEmpty(tenantId))
            node["tenantId"] = tenantId;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"indexes/{Uri.EscapeDataString(index)}/documents")
        {
            Content = JsonContent.Create(new[] { node }, options: JsonOptions),
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string index, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"indexes/{Uri.EscapeDataString(index)}/documents/{Uri.EscapeDataString(id)}");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchResult<T>> SearchAsync<T>(
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

        var body = new Dictionary<string, object?>
        {
            ["q"] = query ?? string.Empty,
            ["limit"] = pageSize,
            ["offset"] = (page - 1) * pageSize,
        };

        if (!string.IsNullOrEmpty(tenantId))
            body["filter"] = $"tenantId = \"{Escape(tenantId)}\"";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"indexes/{Uri.EscapeDataString(index)}/search")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<MeilisearchResponse<T>>(payload, JsonOptions) ?? new();

        return new SearchResult<T>(
            result.Hits ?? [],
            result.EstimatedTotalHits ?? result.Hits?.Count ?? 0,
            page,
            pageSize);
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return response;

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        response.Dispose();

        throw new InvalidOperationException($"Meilisearch 返回 HTTP {status}: {payload}");
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class MeilisearchResponse<T>
    {
        public List<T>? Hits { get; set; }

        public long? EstimatedTotalHits { get; set; }
    }
}
