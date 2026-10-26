using Ledon.Search;
using Ledon.Search.Sync;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>搜索同步边界：租户透传、批次上限与非法 Upsert。</summary>
public class SearchSyncEdgeTests
{
    [Fact]
    public async Task Tenant_id_is_forwarded_to_search()
    {
        var search = new CapturingSearchService();
        var options = new SearchSyncOptions { AutoStart = false };
        var queue = new InMemorySearchSyncQueue(TimeProvider.System, options);
        var service = new SearchSyncService(queue, new SearchSyncProcessor(search, queue, options), options);

        await service.EnqueueUpsertAsync("products", "1", new { Name = "Apple" }, "tenant-a");
        await service.ProcessOnceAsync();

        Assert.Equal("tenant-a", search.LastTenantId);
    }

    [Fact]
    public async Task Batch_size_limits_number_of_changes_processed()
    {
        var search = new CapturingSearchService();
        var options = new SearchSyncOptions { AutoStart = false, BatchSize = 10 };
        var queue = new InMemorySearchSyncQueue(TimeProvider.System, options);
        var service = new SearchSyncService(queue, new SearchSyncProcessor(search, queue, options), options);

        await service.EnqueueUpsertAsync("products", "1", new { Name = "A" });
        await service.EnqueueUpsertAsync("products", "2", new { Name = "B" });
        await service.EnqueueUpsertAsync("products", "3", new { Name = "C" });

        var first = await service.ProcessOnceAsync(maxCount: 2);
        Assert.Equal(2, first.Succeeded);
        Assert.Equal(1, service.PendingCount);

        var second = await service.ProcessOnceAsync(maxCount: 2);
        Assert.Equal(1, second.Succeeded);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task Upsert_without_document_is_rejected()
    {
        var options = new SearchSyncOptions { AutoStart = false };
        var queue = new InMemorySearchSyncQueue(TimeProvider.System, options);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.EnqueueAsync(new SearchIndexChange("products", "1", SearchChangeKind.Upsert)));
    }

    private sealed class CapturingSearchService : ISearchService
    {
        public string? LastTenantId { get; private set; }

        public Task IndexAsync<T>(
            string index,
            string id,
            T document,
            string? tenantId = null,
            CancellationToken cancellationToken = default)
        {
            LastTenantId = tenantId;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string index, string id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SearchResult<T>> SearchAsync<T>(
            string index,
            string? query = null,
            int page = 1,
            int pageSize = 20,
            string? tenantId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResult<T>([], 0, page, pageSize));
    }
}
