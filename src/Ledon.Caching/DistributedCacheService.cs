using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Ledon.Caching;

/// <summary>
/// 基于 <see cref="IDistributedCache"/> 的 <see cref="ICacheService"/> 实现：
/// 多实例部署共享缓存（Redis）；与内存实现同一契约，调用方零改动。
/// 值以 JSON 序列化存储；并发未命中不做 single-flight（分布式场景由 Redis 自身承接，
/// 回源成本高的场景请在业务侧加锁或改用内存实现的 single-flight）。
/// </summary>
internal sealed class DistributedCacheService : ICacheService
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IDistributedCache _cache;
    private readonly LedonDistributedCachingOptions _options;

    public DistributedCacheService(IDistributedCache cache, LedonDistributedCachingOptions options)
    {
        _cache = cache;
        _options = options;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        var bytes = _cache.GetString(FullKey(key));
        if (bytes is null)
        {
            value = default;
            return false;
        }

        value = JsonSerializer.Deserialize<T>(bytes, SerializerOptions);
        return true;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryGet<T>(key, out var value) ? value : default);

    public void Set<T>(string key, T value, TimeSpan? expiration = null) =>
        _cache.SetString(FullKey(key), JsonSerializer.Serialize(value, SerializerOptions), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? _options.DefaultExpiration,
        });

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (TryGet<T>(key, out var hit))
            return hit;

        var value = await factory(cancellationToken);
        Set(key, value, expiration);
        return value;
    }

    public void Remove(string key) => _cache.Remove(FullKey(key));

    private string FullKey(string key) => $"{_options.KeyPrefix}{key}";
}
