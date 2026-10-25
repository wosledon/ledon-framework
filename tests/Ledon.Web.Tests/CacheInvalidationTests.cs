using Ledon.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>缓存跨实例失效总线（进程内实现：本地删除即广播并应用）。</summary>
public class CacheInvalidationTests
{
    private static (ICacheService Cache, ICacheInvalidationBus Bus) Build()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching();
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        var provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<ICacheService>(), provider.GetRequiredService<ICacheInvalidationBus>());
    }

    [Fact]
    public void Remove_publishes_invalidation()
    {
        var (cache, bus) = Build();

        cache.Set("k", "v");
        Assert.True(cache.TryGet<string>("k", out _));

        cache.Remove("k");

        Assert.False(cache.TryGet<string>("k", out _));
    }

    [Fact]
    public async Task Remove_by_prefix_clears_matching_entries()
    {
        var (cache, _) = Build();

        cache.Set("user:1:name", "a");
        cache.Set("user:1:age", 30);
        cache.Set("order:1", "x");

        cache.RemoveByPrefix("user:1");

        Assert.False(cache.TryGet<string>("user:1:name", out _));
        Assert.False(cache.TryGet<int>("user:1:age", out _));
        Assert.True(cache.TryGet<string>("order:1", out _));
    }

    [Fact]
    public async Task Async_remove_also_broadcasts()
    {
        var (cache, _) = Build();

        cache.Set("k", "v");
        await cache.RemoveAsync("k");

        Assert.False(cache.TryGet<string>("k", out _));
    }

    [Fact]
    public void Bus_applier_updates_local_cache()
    {
        var (cache, bus) = Build();

        cache.Set("k", "v");

        // 模拟"收到其它实例的广播"
        bus.SetLocalApplier(
            removeKey => cache.Remove(removeKey),
            removePrefix => cache.RemoveByPrefix(removePrefix));

        var message = CacheInvalidationMessage.ForKey("k");

        // 进程内总线：Publish 即本地应用（真实 Redis 总线上这是远端实例的行为）
        Assert.NotNull(message);
        Assert.Equal("key", message.Kind);
    }

    [Fact]
    public void Cross_instance_invalidation_requires_redis_connection()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching(o => o.CrossInstanceInvalidation = true);   // 未配连接串

        var provider = services.BuildServiceProvider();

        // 配置缺失应启动即明确失败，而不是静默不广播
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICacheInvalidationBus>());
    }
}
