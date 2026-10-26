using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ledon.Security.TokenStores;

/// <summary>共享令牌存储选项。</summary>
public sealed class DistributedTokenStoreOptions
{
    /// <summary>缓存键前缀，默认 <c>ledon:tokens</c>；多应用共用缓存时需区分。</summary>
    public string KeyPrefix { get; set; } = "ledon:tokens";
}

/// <summary>
/// 基于 <see cref="IDistributedCache"/> 的刷新令牌存储：多实例共享（Redis/SQL/内存）。
/// 用户索引用于吊销某用户全部令牌，采用读改写；极端并发下可能漏吊销个别令牌。
/// </summary>
public sealed class DistributedRefreshTokenStore(
    IDistributedCache cache,
    IOptions<DistributedTokenStoreOptions> options) : IRefreshTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DistributedTokenStoreOptions _options = options.Value;

    public async Task StoreAsync(string userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var ttl = expiresAt - DateTimeOffset.UtcNow;

        if (ttl <= TimeSpan.Zero)
            return;

        await cache.SetStringAsync(
            EntryKey(tokenHash),
            JsonSerializer.Serialize(new Entry(userId, expiresAt, Revoked: false), JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken).ConfigureAwait(false);

        await AddToUserIndexAsync(userId, tokenHash, ttl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RefreshTokenEntry?> FindAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var payload = await cache.GetStringAsync(EntryKey(tokenHash), cancellationToken).ConfigureAwait(false);

        if (payload is null)
            return null;

        var entry = JsonSerializer.Deserialize<Entry>(payload, JsonOptions);

        if (entry is null)
            return null;

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await cache.RemoveAsync(EntryKey(tokenHash), cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new RefreshTokenEntry(entry.UserId, entry.ExpiresAt, entry.Revoked);
    }

    public async Task RevokeAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var payload = await cache.GetStringAsync(EntryKey(tokenHash), cancellationToken).ConfigureAwait(false);

        if (payload is null)
            return;

        var entry = JsonSerializer.Deserialize<Entry>(payload, JsonOptions);

        if (entry is null)
            return;

        var ttl = entry.ExpiresAt - DateTimeOffset.UtcNow;

        if (ttl <= TimeSpan.Zero)
            return;

        await cache.SetStringAsync(
            EntryKey(tokenHash),
            JsonSerializer.Serialize(entry with { Revoked = true }, JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var index = await GetUserIndexAsync(userId, cancellationToken).ConfigureAwait(false);

        foreach (var hash in index)
            await RevokeAsync(hash, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddToUserIndexAsync(
        string userId,
        string tokenHash,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        var index = await GetUserIndexAsync(userId, cancellationToken).ConfigureAwait(false);

        if (!index.Contains(tokenHash, StringComparer.Ordinal))
            index.Add(tokenHash);

        await cache.SetStringAsync(
            UserKey(userId),
            JsonSerializer.Serialize(index, JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<string>> GetUserIndexAsync(string userId, CancellationToken cancellationToken)
    {
        var payload = await cache.GetStringAsync(UserKey(userId), cancellationToken).ConfigureAwait(false);

        return payload is null
            ? []
            : JsonSerializer.Deserialize<List<string>>(payload, JsonOptions) ?? [];
    }

    private string EntryKey(string tokenHash) => $"{_options.KeyPrefix}:refresh:{tokenHash}";

    private string UserKey(string userId) => $"{_options.KeyPrefix}:user:{userId}";

    private sealed record Entry(string UserId, DateTimeOffset ExpiresAt, bool Revoked);
}

/// <summary>共享令牌存储注册。</summary>
public static class LedonTokenStoreServiceCollectionExtensions
{
    /// <summary>
    /// 用 <see cref="IDistributedCache"/> 替换默认内存刷新令牌存储：
    /// <code>
    /// builder.Services.AddLedonDistributedCaching(o =&gt; o.RedisConnectionString = "...");
    /// builder.Services.AddLedonDistributedTokenStores();
    /// </code>
    /// 未注册分布式缓存时回退到进程内缓存（仅开发/测试）。
    /// </summary>
    public static IServiceCollection AddLedonDistributedTokenStores(
        this IServiceCollection services,
        Action<DistributedTokenStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new DistributedTokenStoreOptions();
        configure?.Invoke(options);

        services.AddDistributedMemoryCache();
        services.TryAddSingleton(Options.Create(options));
        services.TryAddSingleton<IRefreshTokenStore, DistributedRefreshTokenStore>();

        return services;
    }
}
