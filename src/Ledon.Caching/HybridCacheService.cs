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

/// <summary>HybridCache 不回源信号：工厂抛出即不缓存（用来实现"只读不写"的 GetAsync）。</summary>
internal sealed class CacheMissSignal : Exception;

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
        try
        {
            // HybridCache 无"只读"API：工厂抛出即不缓存——用信号异常实现"未命中不写入"
            return await _cache.GetOrCreateAsync<T?>(
                FullKey(key),
                _ => throw new CacheMissSignal(),
                BuildOptions(key, null),
                Tags(key),
                cancellationToken).ConfigureAwait(false);
        }
        catch (CacheMissSignal)
        {
            return default;
        }
    }

    public void Set<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? slidingExpiration = null) =>
        SetAsync(key, value, expiration, CancellationToken.None).GetAwaiter().GetResult();

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null) =>
        _cache.SetAsync(FullKey(key), value, BuildOptions(key, expiration), Tags(key), cancellationToken).AsTask();

    public Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return _cache.GetOrCreateAsync(
            FullKey(key),
            async token => (T?)(await factory(token).ConfigureAwait(false)),
            BuildOptions(key, expiration),
            Tags(key),
            cancellationToken).AsTask();
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
