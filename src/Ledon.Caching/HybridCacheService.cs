using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Caching;

/// <summary>混合两级缓存配置。</summary>
public sealed class LedonHybridCachingOptions
{
    /// <summary>默认过期时间（L1/L2 同时），默认 5 分钟。</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>所有 key 的前缀，默认空。</summary>
    public string? KeyPrefix { get; set; }
}

/// <summary>
/// 缓存值包装：区分"缓存了 null"与"未命中"（HybridCache 没有只读 API，
/// GetAsync 需要工厂；工厂返回 Miss 实例表示未命中且不写入缓存）。
/// 用值对象而不是异常——未命中是高频路径，不该付异常开销。
/// 必须是可 JSON 序列化的具体类型（L2 分布式缓存要落盘）。
/// </summary>
internal sealed class CacheValue<T>
{
    // System.Text.Json 反序列化需要（L2 分布式缓存落盘/读盘）
    public CacheValue()
    {
    }

    private CacheValue(T? value, bool hit) => (Value, IsHit) = (value, hit);

    /// <summary>是否命中（false = 未命中哨兵，不写入缓存）。</summary>
    public bool IsHit { get; set; }

    /// <summary>缓存的值（未命中时为 default）。</summary>
    public T? Value { get; set; }

    /// <summary>未命中哨兵。</summary>
    public static CacheValue<T> Miss { get; } = new() { IsHit = false };

    /// <summary>命中包装。</summary>
    public static CacheValue<T> Hit(T? value) => new() { IsHit = true, Value = value };

    /// <summary>取出值；未命中返回 fallback。</summary>
    public T? ValueOr(T? fallback) => IsHit ? Value : fallback;
}

/// <summary>
/// 基于 .NET 10 <see cref="HybridCache"/> 的 <see cref="ICacheService"/> 实现：
/// L1 进程内内存 + L2 分布式缓存（注册了 <c>IDistributedCache</c> 时自动启用，
/// 如 <c>AddLedonDistributedCaching</c> / <c>AddStackExchangeRedisCache</c>）。
/// 价值：读多写少的热数据 L1 命中零网络往返，跨实例一致性由 L2 保证。
/// <para>
/// 前缀删除语义：写入时按 key 的命名空间（首段，<c>users:1</c> → <c>users</c>）打标签，
/// <c>RemoveByPrefix("users")</c> 经 HybridCache 标签删除全局生效（L1+L2，多实例）——
/// 因此 key 请按 <c>命名空间:键</c> 约定书写；不带 <c>:</c> 的键标签即键本身。
/// 滑动过期在两级缓存下无意义（L2 无法表达"未访问"），忽略。
/// </para>
/// </summary>
internal sealed class HybridCacheService : ICacheService
{
    private readonly HybridCache _cache;
    private readonly LedonHybridCachingOptions _options;
    private readonly ICacheKeyPrefixProvider? _prefixProvider;

    public HybridCacheService(
        HybridCache cache,
        LedonHybridCachingOptions options,
        IServiceProvider services)
    {
        _cache = cache;
        _options = options;
        _prefixProvider = services.GetService<ICacheKeyPrefixProvider>();
    }

    public bool TryGet<T>(string key, out T? value)
    {
        value = GetAsync<T>(key, CancellationToken.None).GetAwaiter().GetResult();
        return value is not null;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        // HybridCache 没有只读 API；工厂返回 Miss 哨兵表示未命中且不写入缓存——
        // 用值对象而非异常：未命中是高频路径，不该付异常开销
        var cached = await _cache.GetOrCreateAsync<CacheValue<T>>(
            FullKey(key),
            static _ => new ValueTask<CacheValue<T>>(CacheValue<T>.Miss),
            BuildOptions(key, null),
            Tags(key),
            cancellationToken).ConfigureAwait(false);

        return cached.ValueOr(default);
    }

    public void Set<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? slidingExpiration = null) =>
        SetAsync(key, value, expiration, CancellationToken.None).GetAwaiter().GetResult();

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null) =>
        // 与 GetAsync 对称地写包装（GetAsync 读包装类型；两边不一致会读到 null）
        _cache.SetAsync(
            FullKey(key),
            CacheValue<T>.Hit(value),
            BuildOptions(key, expiration),
            Tags(key),
            cancellationToken).AsTask();

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // HybridCache 原生 single-flight：并发未命中共享同一次回源。
        // GetAsync 的 Miss 哨兵可能已被 HybridCache 写入（框架不提供工厂短路），
        // 这里读到 Miss 视为未命中，回源后覆写
        var cached = await _cache.GetOrCreateAsync<CacheValue<T>>(
            FullKey(key),
            async token => CacheValue<T>.Hit(await factory(token).ConfigureAwait(false)),
            BuildOptions(key, expiration),
            Tags(key),
            cancellationToken).ConfigureAwait(false);

        if (!cached.IsHit)
        {
            var value = await factory(cancellationToken).ConfigureAwait(false);
            await SetAsync(key, value, expiration, cancellationToken).ConfigureAwait(false);
            return value;
        }

        return cached.ValueOr(default);
    }

    public void Remove(string key) =>
        RemoveAsync(key, CancellationToken.None).GetAwaiter().GetResult();

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(FullKey(key), cancellationToken).AsTask();

    public void RemoveByPrefix(string prefix) =>
        RemoveByPrefixAsync(prefix, CancellationToken.None).GetAwaiter().GetResult();

    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        // 标签 = 命名空间（键首段，含动态前缀）：删除经 L1+L2 全局生效
        return _cache.RemoveByTagAsync(NamespaceOf(FullKey(prefix)), cancellationToken).AsTask();
    }

    private HybridCacheEntryOptions BuildOptions(string key, TimeSpan? expiration) =>
        new()
        {
            Expiration = expiration ?? _options.DefaultExpiration,
            LocalCacheExpiration = expiration ?? _options.DefaultExpiration,
        };

    private string[] Tags(string key) => [NamespaceOf(key)];

    /// <summary>键的命名空间（首段）：<c>users:1</c> → <c>users</c>；无分隔符时即键本身。</summary>
    private static string NamespaceOf(string key)
    {
        var separator = key.IndexOf(':');
        return separator < 0 ? key : key[..separator];
    }

    private string FullKey(string key)
    {
        var prefix = _prefixProvider?.GetKeyPrefix();
        if (string.IsNullOrEmpty(prefix))
            prefix = _options.KeyPrefix;

        return string.IsNullOrEmpty(prefix) ? key : $"{prefix}{key}";
    }
}
