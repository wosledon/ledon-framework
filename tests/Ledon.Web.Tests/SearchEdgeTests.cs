using Ledon.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>搜索配置边界：必填项、非法枚举/超时、未知索引。</summary>
public class SearchEdgeTests
{
    [Fact]
    public void Meilisearch_without_endpoint_is_rejected()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLedonSearch(options => options.Provider = SearchProvider.Meilisearch));

        Assert.Contains("Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_positive_timeout_is_rejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonSearch(options => options.Timeout = TimeSpan.Zero));
    }

    [Fact]
    public void Undefined_provider_is_rejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonSearch(options => options.Provider = (SearchProvider)99));
    }

    [Fact]
    public async Task Unknown_index_returns_empty_result()
    {
        var search = new InMemorySearchService();

        var result = await search.SearchAsync<Doc>("missing-index", "anything");

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    private sealed record Doc(string Id, string Name);
}
