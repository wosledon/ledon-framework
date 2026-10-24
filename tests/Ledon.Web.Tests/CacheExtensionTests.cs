using Ledon.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 缓存契约扩展测试：滑动过期、异步读写、前缀删除，以及混合两级缓存
/// （.NET 10 HybridCache：L1 内存 + L2 分布式，未命中不写、按命名空间标签删除）。
/// </summary>
public class CacheExtensionTests
{
    // ---------- 内存缓存 ----------

    [Fact]
    public async Task Memory_cache_supports_async_set_get_and_remove()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("k", "v");

        Assert.Equal("v", await cache.GetAsync<string>("k"));

        await cache.RemoveAsync("k");

        Assert.Null(await cache.GetAsync<string>("k"));
    }

    [Fact]
    public void Memory_cache_remove_by_prefix_only_removes_matching_keys()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        cache.Set("users:1", "a");
        cache.Set("users:2", "b");
        cache.Set("orders:1", "c");

        cache.RemoveByPrefix("users:");

        Assert.False(cache.TryGet<string>("users:1", out _));
        Assert.False(cache.TryGet<string>("users:2", out _));
        Assert.True(cache.TryGet<string>("orders:1", out _));
    }

    [Fact]
    public void Memory_cache_sliding_expiration_keeps_entry_while_accessed()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching(o => o.DefaultExpiration = TimeSpan.FromMinutes(10));
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        // 绝对过期 10 分钟、滑动过期 200ms：持续访问应保持有效
        cache.Set("k", "v", TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < 4; i++)
        {
            Assert.True(cache.TryGet<string>("k", out _), $"第 {i} 次访问前条目已失效");
            Thread.Sleep(80);
        }

        // 停止访问超过滑动窗口：条目失效
        Thread.Sleep(400);
        Assert.False(cache.TryGet<string>("k", out _));
    }

    // ---------- 混合两级缓存 ----------

    [Fact]
    public async Task Hybrid_cache_get_or_create_hits_factory_once()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();   // L2（进程内分布式：单测等价物）
        services.AddLedonHybridCaching();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var calls = 0;
        Task<string> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult("v");
        }

        var first = await cache.GetOrCreateAsync("users:1", Factory);
        var second = await cache.GetOrCreateAsync("users:1", Factory);

        Assert.Equal("v", first);
        Assert.Equal("v", second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Hybrid_cache_get_async_does_not_cache_a_miss()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddLedonHybridCaching();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        // GetAsync 未命中不写入缓存
        Assert.Null(await cache.GetAsync<string>("users:missing"));

        // 后续 GetOrCreateAsync 仍会回源（而不是拿到"缓存的 null"）
        var calls = 0;
        var value = await cache.GetOrCreateAsync("users:missing", _ =>
        {
            calls++;
            return Task.FromResult("fresh");
        });

        Assert.Equal("fresh", value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Hybrid_cache_remove_by_prefix_removes_namespace_tagged_entries()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddLedonHybridCaching();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("users:1", "a");
        await cache.SetAsync("users:2", "b");
        await cache.SetAsync("orders:1", "c");

        await cache.RemoveByPrefixAsync("users");

        Assert.Null(await cache.GetAsync<string>("users:1"));
        Assert.Null(await cache.GetAsync<string>("users:2"));
        Assert.Equal("c", await cache.GetAsync<string>("orders:1"));
    }

    [Fact]
    public async Task Hybrid_cache_set_and_remove_roundtrip()
    {
        var services = new ServiceCollection();
        services.AddLedonHybridCaching();   // 无 L2：纯 L1
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        cache.Set("k", "v");
        Assert.True(cache.TryGet<string>("k", out var read));
        Assert.Equal("v", read);

        await cache.RemoveAsync("k");
        Assert.Null(await cache.GetAsync<string>("k"));
    }}
