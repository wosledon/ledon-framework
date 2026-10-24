using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Ledon.Caching;

/// <summary>
/// 基于 <see cref="IDistributedCache"/> 的 <see cref="ICacheService"/> 实现：
/// 多实例部署共享缓存（Redis）；与内存实现同一契约，调用方零改动。
/// 值以 JSON 序列化存储；并发未命中不做 single-flight（分布式场景由 Redis 自身承接，
/// 回源成本高的场景请在业务侧加锁或改用内存实现的 single-flight）。
/// 前缀删除：Redis 场景经 <see cref="IConnectionMultiplexer"/> 的 SCAN 模式匹配全局生效；
/// 非 Redis 提供程序（无可解析的 multiplexer）时为空操作（只有本进程内存索引可用，已由
/// <c>AddLedonDistributedCaching</c> 在 Redis 配置下注册 multiplexer 保证）。
/// </summary>
internal sealed class DistributedCacheService : ICacheService
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IDistributedCache _cache;
    private readonly LedonDistributedCachingOptions _options;
    private readonly IConnectionMultiplexer? _multiplexer;

    public DistributedCacheService(
        IDistributedCache cache,
        LedonDistributedCachingOptions options,
        IConnectionMultiplexer? multiplexer = null)
    {
        _cache = cache;
        _options = options;
        _multiplexer = multiplexer;
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

    public void Set<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? slidingExpiration = null) =>
        _cache.SetString(FullKey(key), JsonSerializer.Serialize(value, SerializerOptions), BuildOptions(expiration, slidingExpiration));

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null) =>
        _cache.SetStringAsync(
            FullKey(key),
            JsonSerializer.Serialize(value, SerializerOptions),
            BuildOptions(expiration, slidingExpiration),
            cancellationToken);

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (TryGet<T>(key, out var hit))
            return hit;

        var value = await factory(cancellationToken).ConfigureAwait(false);
        await SetAsync(key, value, expiration, cancellationToken, slidingExpiration).ConfigureAwait(false);
        return value;
    }

    public void Remove(string key) => _cache.Remove(FullKey(key));

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(FullKey(key), cancellationToken);

    public void RemoveByPrefix(string prefix) =>
        RemoveByPrefixAsync(prefix, CancellationToken.None).GetAwaiter().GetResult();

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (_multiplexer is null)
            return;   // 非 Redis 提供程序：无法枚举 key，跳过（调用方无感知成本）

        var pattern = $"{_options.KeyPrefix}{prefix}*";
        var database = _multiplexer.GetDatabase();

        foreach (var server in _multiplexer.GetServers())
        {
            await foreach (var key in server.KeysAsync(pattern: pattern)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                await database.KeyDeleteAsync(key).ConfigureAwait(false);
            }
        }
    }

    private DistributedCacheEntryOptions BuildOptions(TimeSpan? expiration, TimeSpan? slidingExpiration) =>
        new()
        {
            AbsoluteExpirationRelativeToNow = expiration ?? _options.DefaultExpiration,
            SlidingExpiration = slidingExpiration ?? _options.DefaultSlidingExpiration,
        };

    private string FullKey(string key) => $"{_options.KeyPrefix}{key}";
}
