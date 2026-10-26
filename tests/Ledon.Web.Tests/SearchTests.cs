using System.Net;
using System.Text;
using System.Text.Json;
using Ledon.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>搜索：内存索引/过滤/分页/删除，Meilisearch 请求格式与响应解析。</summary>
public class SearchTests
{
    [Fact]
    public async Task In_memory_search_filters_and_pages()
    {
        var search = new InMemorySearchService();

        await search.IndexAsync("products", "1", new Product("1", "Phone Pro", "tenant-a"), "tenant-a");
        await search.IndexAsync("products", "2", new Product("2", "Phone Mini", "tenant-a"), "tenant-a");
        await search.IndexAsync("products", "3", new Product("3", "Phone Max", "tenant-b"), "tenant-b");
        await search.IndexAsync("products", "4", new Product("4", "Laptop", "tenant-a"), "tenant-a");

        var page = await search.SearchAsync<Product>("products", "phone", page: 1, pageSize: 1, tenantId: "tenant-a");

        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal("Phone Pro", page.Items[0].Name);

        var second = await search.SearchAsync<Product>("products", "phone", page: 2, pageSize: 1, tenantId: "tenant-a");
        Assert.Equal("Phone Mini", second.Items[0].Name);

        await search.DeleteAsync("products", "1");
        var afterDelete = await search.SearchAsync<Product>("products", "phone", tenantId: "tenant-a");
        Assert.Equal(1, afterDelete.Total);
    }

    [Fact]
    public async Task Meilisearch_indexes_and_searches_with_tenant_filter()
    {
        var handler = new FakeMeilisearchHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSearch(o =>
        {
            o.Provider = SearchProvider.Meilisearch;
            o.Endpoint = "http://meili.test";
            o.ApiKey = "key";
            o.HttpMessageHandler = handler;
        });

        await using var provider = services.BuildServiceProvider();
        var search = provider.GetRequiredService<ISearchService>();

        await search.IndexAsync("products", "1", new Product("1", "Phone", "tenant-a"), "tenant-a");
        var result = await search.SearchAsync<Product>(
            "products", "phone", page: 1, pageSize: 10, tenantId: "tenant-a");

        Assert.Single(result.Items);
        Assert.Equal("Phone", result.Items[0].Name);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer key", handler.Requests[0].Authorization);
        Assert.Contains("/indexes/products/documents", handler.Requests[0].Url);
        Assert.Contains("\"tenantId\":\"tenant-a\"", handler.Requests[0].Body);

        Assert.Contains("/indexes/products/search", handler.Requests[1].Url);
        Assert.Contains("tenantId", handler.Requests[1].Body);
    }

    public sealed record Product(string Id, string Name, string TenantId);

    private sealed class FakeMeilisearchHandler : HttpMessageHandler
    {
        public List<CapturedSearchRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedSearchRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString() ?? "",
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));

            var body = request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? """{"hits":[{"id":"1","name":"Phone","tenantId":"tenant-a"}],"estimatedTotalHits":1}"""
                : """{"taskUid":1}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedSearchRequest(string Url, string Authorization, string Body);
}
