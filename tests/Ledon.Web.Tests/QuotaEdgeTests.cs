using Ledon.Quotas;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>配额边界：分布式计数、零上限与窗口独立。</summary>
public class QuotaEdgeTests
{
    [Fact]
    public async Task Distributed_store_increments_fixed_window()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new DistributedQuotaStore(cache, new QuotaOptions());
        var window = TimeSpan.FromMinutes(1);

        var first = await store.IncrementAsync("tenant-a", window);
        var second = await store.IncrementAsync("tenant-a", window);
        var other = await store.IncrementAsync("tenant-b", window);

        Assert.Equal(1, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(1, other.Count);
        Assert.True(second.ResetAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Zero_limit_denies_without_consuming_quota()
    {
        var service = new QuotaService(new InMemoryQuotaStore(TimeProvider.System));

        var denied = await service.ConsumeAsync("key", limit: 0, TimeSpan.FromMinutes(1));
        var allowed = await service.ConsumeAsync("key", limit: 1, TimeSpan.FromMinutes(1));

        Assert.False(denied.Allowed);
        Assert.Equal(0, denied.Remaining);
        Assert.True(allowed.Allowed);
        Assert.Equal(0, allowed.Remaining);
    }
}
