using Ledon.Search;
using Ledon.Search.Sync;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>搜索同步：入队、删除、重试与丢弃。</summary>
public class SearchSyncTests
{
    [Fact]
    public async Task Upsert_and_delete_are_applied()
    {
        var (service, search) = CreateService();

        await service.EnqueueUpsertAsync("products", "1", new SyncProduct { Name = "Apple" });
        var upsert = await service.ProcessOnceAsync();

        Assert.Equal(1, upsert.Succeeded);
        var found = await search.SearchAsync<SyncProduct>("products", "Apple");
        Assert.Single(found.Items);

        await service.EnqueueDeleteAsync("products", "1");
        var delete = await service.ProcessOnceAsync();

        Assert.Equal(1, delete.Succeeded);
        Assert.Equal(0, (await search.SearchAsync<SyncProduct>("products")).Total);
    }

    [Fact]
    public async Task Failed_index_is_retried_until_success()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = new SearchSyncOptions { MaxRetries = 3, RetryDelay = TimeSpan.Zero, BatchSize = 10 };
        var search = new FlakySearchService(failures: 2);
        var service = CreateService(search, time, options);

        await service.EnqueueUpsertAsync("products", "1", new SyncProduct { Name = "Apple" });

        var first = await service.ProcessOnceAsync();
        Assert.Equal(1, first.Failed);

        var second = await service.ProcessOnceAsync();
        Assert.Equal(1, second.Failed);

        var third = await service.ProcessOnceAsync();
        Assert.Equal(1, third.Succeeded);
        Assert.Equal(3, search.IndexCalls);
    }

    [Fact]
    public async Task Exceeding_retry_limit_drops_change()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = new SearchSyncOptions { MaxRetries = 1, RetryDelay = TimeSpan.Zero };
        var search = new FlakySearchService(failures: 10);
        var service = CreateService(search, time, options);

        await service.EnqueueUpsertAsync("products", "1", new SyncProduct { Name = "Apple" });
        var result = await service.ProcessOnceAsync();

        Assert.Equal(1, result.Dropped);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public void Di_registration_resolves_sync_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISearchService>(new InMemorySearchService());
        services.AddLedonSearchSync(o => o.AutoStart = false);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<SearchSyncService>());
        Assert.NotNull(provider.GetRequiredService<ISearchSyncQueue>());
    }

    private static (SearchSyncService Service, ISearchService Search) CreateService()
    {
        var search = new InMemorySearchService();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        return (CreateService(search, time, new SearchSyncOptions { AutoStart = false }), search);
    }

    private static SearchSyncService CreateService(
        ISearchService search,
        TimeProvider time,
        SearchSyncOptions options)
    {
        var queue = new InMemorySearchSyncQueue(time, options);
        var processor = new SearchSyncProcessor(search, queue, options);

        return new SearchSyncService(queue, processor, options);
    }

    private sealed class FlakySearchService(int failures) : ISearchService
    {
        private readonly InMemorySearchService _inner = new();

        public int IndexCalls { get; private set; }

        public Task IndexAsync<T>(
            string index,
            string id,
            T document,
            string? tenantId = null,
            CancellationToken cancellationToken = default)
        {
            IndexCalls++;

            if (IndexCalls <= failures)
                throw new InvalidOperationException("搜索服务暂不可用");

            return _inner.IndexAsync(index, id, document, tenantId, cancellationToken);
        }

        public Task DeleteAsync(string index, string id, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(index, id, cancellationToken);

        public Task<SearchResult<T>> SearchAsync<T>(
            string index,
            string? query = null,
            int page = 1,
            int pageSize = 20,
            string? tenantId = null,
            CancellationToken cancellationToken = default) =>
            _inner.SearchAsync<T>(index, query, page, pageSize, tenantId, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class SyncProduct
{
    public string Name { get; set; } = string.Empty;
}
