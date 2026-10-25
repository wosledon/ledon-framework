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
    /// <summary>负缓存哨兵：与 <c>null</c> 区分开（JSON null 也是合法缓存值）。</summary>
    private const string MissSentinel = "\"\u0000ledon:miss\"";

    private readonly IDistributedCache _cache;
    private readonly LedonDistributedCachingOptions _options;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly ICacheKeyPrefixProvider? _prefixProvider;
    private readonly JsonSerializerOptions _serializerOptions;

    public DistributedCacheService(
        IDistributedCache cache,
        LedonDistributedCachingOptions options,
        IConnectionMultiplexer? multiplexer = null,
        ICacheKeyPrefixProvider? prefixProvider = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        _cache = cache;
        _options = options;
        _multiplexer = multiplexer;
        _prefixProvider = prefixProvider;

        // 序列化选项可注入：命名策略/多态/自定义转换器都由调用方定。
        // 硬编码 new JsonSerializerOptions() 会让跨服务的事件载荷演化失去弹性
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions();
    }

    public bool TryGet<T>(string key, out T? value)
    {
        var bytes = _cache.GetString(FullKey(key));
        if (bytes is null)
        {
            value = default;
            return false;
        }

        if (bytes == MissSentinel)
        {
            // 负缓存命中：按"未命中"处理，但不回源（防穿透）
            value = default;
            return false;
        }

        value = JsonSerializer.Deserialize<T>(bytes, _serializerOptions);
        return true;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryGet<T>(key, out var value) ? value : default);

    public void Set<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? slidingExpiration = null) =>
        _cache.SetString(FullKey(key), JsonSerializer.Serialize(value, _serializerOptions), BuildOptions(expiration, slidingExpiration));

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null) =>
        _cache.SetStringAsync(
            FullKey(key),
            JsonSerializer.Serialize(value, _serializerOptions),
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

        var fullKey = FullKey(key);

        if (TryGet<T>(key, out var hit))
            return hit;

        // 负缓存哨兵还在：说明刚才有人回源拿到 null，直接复用结论，不再打底层存储
        if (_cache.GetString(fullKey) == MissSentinel)
            return default;

        var value = await factory(cancellationToken).ConfigureAwait(false);

        if (value is null && _options.NegativeExpiration > TimeSpan.Zero)
            await _cache.SetStringAsync(fullKey, MissSentinel, BuildOptions(_options.NegativeExpiration, null), cancellationToken)
                .ConfigureAwait(false);
        else if (value is not null)
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

    private string FullKey(string key)
    {
        var prefix = _prefixProvider?.GetKeyPrefix();
        if (string.IsNullOrEmpty(prefix))
            prefix = _options.KeyPrefix;

        return $"{prefix}{key}";
    }
}
