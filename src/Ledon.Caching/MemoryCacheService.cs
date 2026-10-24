using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Caching;

/// <summary>
/// 基于 <see cref="IMemoryCache"/> 的 <see cref="ICacheService"/> 实现：
/// 支持 key 前缀、默认过期时间、滑动过期，以及对同一 key 并发未命中的 single-flight 回源
/// （并发调用共享同一个回源 Task，不会重复回源；回源失败时移除在途记录，下次可重试）。
/// 前缀删除基于进程内 key 索引（随条目逐出/移除同步清理）。
/// </summary>
internal sealed class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly LedonCachingOptions _options;
    private readonly ICacheKeyPrefixProvider? _prefixProvider;

    /// <summary>进行中的回源任务（key 为完整 key）：保证同一 key 只回源一次。</summary>
    private readonly ConcurrentDictionary<string, object> _inFlight = new();

    /// <summary>进程内 key 索引：前缀删除用。条目逐出/移除时经回调清理，不随缓存无限增长。</summary>
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    public MemoryCacheService(
        IMemoryCache cache,
        LedonCachingOptions options,
        IServiceProvider services)
    {
        _cache = cache;
        _options = options;
        // 动态前缀提供者（多租户隔离）：单例安全的实现（读环境/静态状态）
        _prefixProvider = services.GetService<ICacheKeyPrefixProvider>();
    }

    public bool TryGet<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(FullKey(key), out var cached) && cached is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryGet<T>(key, out var value) ? value : default);

    public void Set<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? slidingExpiration = null) =>
        SetCore(FullKey(key), value, expiration, slidingExpiration);

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null)
    {
        Set(key, value, expiration, slidingExpiration);
        return Task.CompletedTask;
    }

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var fullKey = FullKey(key);
        if (_cache.TryGetValue(fullKey, out var cached))
            return cached is T hit ? hit : default;

        // single-flight：并发未命中共享同一个回源 Task（无锁，不会锁住异步）
        // 回源使用 CancellationToken.None——共享任务不绑定任何单个调用者，
        // 否则一个调用者取消会杀掉其他所有等待者的回源；
        // 每个调用者通过 WaitAsync 观察自己的取消令牌。
        var task = (Task<T>)_inFlight.GetOrAdd(fullKey, _ => factory(CancellationToken.None));

        T value;
        try
        {
            value = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 任务完成后才移除在途记录；调用者取消但任务仍在跑时保留，让后来者继续共享
            if (task.IsCompleted)
                _inFlight.TryRemove(fullKey, out _);
        }

        SetCore(fullKey, value, expiration, slidingExpiration);
        return value;
    }

    public void Remove(string key)
    {
        var fullKey = FullKey(key);
        _keys.TryRemove(fullKey, out _);
        _cache.Remove(fullKey);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }

    public void RemoveByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var fullPrefix = FullKey(prefix);
        foreach (var key in _keys.Keys.Where(k => k.StartsWith(fullPrefix, StringComparison.Ordinal)))
        {
            _keys.TryRemove(key, out _);
            _cache.Remove(key);
        }
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        RemoveByPrefix(prefix);
        return Task.CompletedTask;
    }

    private void SetCore<T>(string fullKey, T value, TimeSpan? expiration = null, TimeSpan? slidingExpiration = null)
    {
        var entryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? _options.DefaultExpiration,
            SlidingExpiration = slidingExpiration ?? _options.DefaultSlidingExpiration,
        };

        // 索引维护：写入登记、逐出/移除注销——索引不随缓存无限增长
        entryOptions.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
        {
            EvictionCallback = (key, _, _, _) => _keys.TryRemove((string)key, out _),
        });
        _keys[fullKey] = 0;

        _cache.Set(fullKey, value, entryOptions);
    }

    private string FullKey(string key)
    {
        var prefix = _prefixProvider?.GetKeyPrefix();
        if (string.IsNullOrEmpty(prefix))
            prefix = _options.KeyPrefix;

        return string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";
    }
}
