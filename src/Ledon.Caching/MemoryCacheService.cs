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
    /// <summary>负缓存哨兵值（放进单独的 miss key，避免污染正常缓存值）。</summary>
    private const string MissMarker = "\u0000miss";

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

        // 负缓存哨兵：刚才有人回源拿到 null，直接复用结论不再回源
        if (cached is null && _cache.TryGetValue(MissKey(fullKey), out _))
            return default;

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

        // 回源拿到 null：写负缓存哨兵（防穿透）；关闭负缓存时什么都不写——
        // 绝不能把 null 当正常值缓存，否则后续查询全部"命中 null"再也不回源
        if (value is null)
        {
            if (_options.NegativeExpiration > TimeSpan.Zero)
                SetCore(MissKey(fullKey), MissMarker, _options.NegativeExpiration, null);
        }
        else
        {
            SetCore(fullKey, value, expiration, slidingExpiration);
        }

        return value;
    }

    public void Remove(string key)
    {
        var fullKey = FullKey(key);
        _keys.TryRemove(fullKey, out _);
        _cache.Remove(fullKey);

        // 负缓存哨兵一起清：显式失效就该让下次真的回源
        var missKey = MissKey(fullKey);
        _keys.TryRemove(missKey, out _);
        _cache.Remove(missKey);
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

    /// <summary>收到其它实例的广播：删本进程对应键（含静态前缀的完整键）。</summary>
    private void ApplyRemoteKeyRemoval(string key)
    {
        var fullKey = FullKey(key);
        _keys.TryRemove(fullKey, out _);
        _cache.Remove(fullKey);
    }

    /// <summary>收到其它实例的广播：删本进程该前缀下全部键。</summary>
    private void ApplyRemotePrefixRemoval(string prefix)
    {
        var fullPrefix = FullKey(prefix);
        foreach (var key in _keys.Keys.Where(k => k.StartsWith(fullPrefix, StringComparison.Ordinal)))
        {
            _keys.TryRemove(key, out _);
            _cache.Remove(key);
        }
    }

    private string FullKey(string key)
    {
        var prefix = _prefixProvider?.GetKeyPrefix();
        if (string.IsNullOrEmpty(prefix))
            prefix = _options.KeyPrefix;

        return string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";
    }

    /// <summary>负缓存哨兵的 key（与正常 key 分开，Remove 正常 key 时不会误删）。</summary>
    private static string MissKey(string fullKey) => $"{fullKey}\u0000miss";
}
