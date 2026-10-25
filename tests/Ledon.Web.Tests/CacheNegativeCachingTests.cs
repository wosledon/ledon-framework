using Ledon.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 负缓存（防穿透）：回源返回 null 时按短 TTL 缓存"未命中"，
/// 让同一 key 的后续请求不再打到底层存储。
/// </summary>
public class CacheNegativeCachingTests
{
    [Fact]
    public async Task Memory_cache_caches_misses_and_skips_refetch()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching(o => o.NegativeExpiration = TimeSpan.FromSeconds(30));
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var calls = 0;

        Task<string?> MissAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        }

        // 第一次：回源一次，拿到 null
        Assert.Null(await cache.GetOrCreateAsync("missing", MissAsync));
        Assert.Equal(1, calls);

        // 第二次：命中负缓存哨兵，不再回源
        Assert.Null(await cache.GetOrCreateAsync("missing", MissAsync));
        Assert.Equal(1, calls);

        // 显式移除后恢复回源
        cache.Remove("missing");
        Assert.Null(await cache.GetOrCreateAsync("missing", MissAsync));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Distributed_cache_caches_misses_and_skips_refetch()
    {
        var services = new ServiceCollection();
        services.AddLedonDistributedCaching(o => o.NegativeExpiration = TimeSpan.FromSeconds(30));
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var calls = 0;

        Task<string?> MissAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        }

        Assert.Null(await cache.GetOrCreateAsync("gone", MissAsync));
        Assert.Equal(1, calls);

        Assert.Null(await cache.GetOrCreateAsync("gone", MissAsync));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Null_values_are_not_confused_with_misses()
    {
        // 哨兵是独立存储的：缓存一个真正的 null 之后，同一个 key 仍然只回源一次
        var services = new ServiceCollection();
        services.AddLedonCaching(o => o.NegativeExpiration = TimeSpan.FromMinutes(5));
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var calls = 0;

        Task<string?> MissAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        }

        for (var i = 0; i < 5; i++)
            Assert.Null(await cache.GetOrCreateAsync("same-key", MissAsync));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Negative_caching_can_be_disabled()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching(o => o.NegativeExpiration = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var calls = 0;

        Task<string?> MissAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        }

        await cache.GetOrCreateAsync("no-neg", MissAsync);
        await cache.GetOrCreateAsync("no-neg", MissAsync);

        // 关闭负缓存：每次都回源
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Distributed_cache_accepts_custom_serializer_options()
    {
        var services = new ServiceCollection();
        services.AddLedonDistributedCaching();
        services.AddSingleton(new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

        using var provider = services.BuildServiceProvider();

        // 选项可注入：构造不抛即证明 DI 链通了（序列化行为由调用方定）
        Assert.NotNull(provider.GetRequiredService<ICacheService>());
    }
}
