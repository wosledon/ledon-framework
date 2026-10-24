using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Ledon.Caching;

/// <summary>
/// 基于 <see cref="IMemoryCache"/> 的 <see cref="ICacheService"/> 实现：
/// 支持 key 前缀、默认过期时间，以及对同一 key 并发未命中的 single-flight 回源
/// （并发调用共享同一个回源 Task，不会重复回源；回源失败时移除在途记录，下次可重试）。
/// </summary>
internal sealed class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly LedonCachingOptions _options;

    /// <summary>进行中的回源任务（key 为完整 key）：保证同一 key 只回源一次。</summary>
    private readonly ConcurrentDictionary<string, object> _inFlight = new();

    public MemoryCacheService(IMemoryCache cache, LedonCachingOptions options)
    {
        _cache = cache;
        _options = options;
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

    public void Set<T>(string key, T value, TimeSpan? expiration = null) =>
        SetCore(FullKey(key), value, expiration);

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var fullKey = FullKey(key);
        if (_cache.TryGetValue(fullKey, out var cached))
            return cached is T hit ? hit : default;

        // single-flight：并发未命中共享同一个回源 Task（无锁，不会锁住异步）
        var task = (Task<T>)_inFlight.GetOrAdd(fullKey, _ => factory(cancellationToken));

        T value;
        try
        {
            value = await task;
        }
        finally
        {
            _inFlight.TryRemove(fullKey, out _);
        }

        SetCore(fullKey, value, expiration);
        return value;
    }

    public void Remove(string key) => _cache.Remove(FullKey(key));

    private void SetCore<T>(string fullKey, T value, TimeSpan? expiration = null) =>
        _cache.Set(fullKey, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? _options.DefaultExpiration,
        });

    private string FullKey(string key) =>
        string.IsNullOrEmpty(_options.KeyPrefix) ? key : $"{_options.KeyPrefix}:{key}";
}
