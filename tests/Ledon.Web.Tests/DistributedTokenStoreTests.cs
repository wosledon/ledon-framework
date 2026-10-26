using Ledon.Security;
using Ledon.Security.TokenStores;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>共享刷新令牌存储：跨实例查找、吊销、按用户全量吊销、过期。</summary>
public class DistributedTokenStoreTests
{
    [Fact]
    public async Task Shared_store_is_visible_across_providers()
    {
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));

        await using var first = Build(cache);
        await using var second = Build(cache);

        var writer = first.GetRequiredService<IRefreshTokenStore>();
        var reader = second.GetRequiredService<IRefreshTokenStore>();

        await writer.StoreAsync("u1", "hash-1", DateTimeOffset.UtcNow.AddMinutes(5));

        var found = await reader.FindAsync("hash-1");
        Assert.NotNull(found);
        Assert.Equal("u1", found.UserId);
        Assert.False(found.Revoked);
    }

    [Fact]
    public async Task Revoke_and_revoke_all_work()
    {
        await using var provider = Build();
        var store = provider.GetRequiredService<IRefreshTokenStore>();

        await store.StoreAsync("u1", "a", DateTimeOffset.UtcNow.AddMinutes(5));
        await store.StoreAsync("u1", "b", DateTimeOffset.UtcNow.AddMinutes(5));
        await store.StoreAsync("u2", "c", DateTimeOffset.UtcNow.AddMinutes(5));

        await store.RevokeAsync("a");
        Assert.True((await store.FindAsync("a"))!.Revoked);

        await store.RevokeAllForUserAsync("u1");
        Assert.True((await store.FindAsync("b"))!.Revoked);
        Assert.False((await store.FindAsync("c"))!.Revoked);
    }

    [Fact]
    public async Task Expired_entries_are_not_returned()
    {
        await using var provider = Build();
        var store = provider.GetRequiredService<IRefreshTokenStore>();

        await store.StoreAsync("u1", "expired", DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.Null(await store.FindAsync("expired"));
    }

    private static ServiceProvider Build(IDistributedCache? cache = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(cache ?? new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())));
        services.AddLedonDistributedTokenStores();

        return services.BuildServiceProvider();
    }
}
