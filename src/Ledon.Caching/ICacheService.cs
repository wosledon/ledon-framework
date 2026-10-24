namespace Ledon.Caching;

/// <summary>
/// 缓存服务契约。按 key 读写，未命中时用工厂回源并写入。
/// v1 为进程内内存实现；需要分布式缓存（Redis 等）时替换此实现即可，
/// 调用方代码不变（契约与实现分离，换实现不改业务）。
/// </summary>
public interface ICacheService
{
    /// <summary>尝试读取缓存；未命中返回 false。</summary>
    bool TryGet<T>(string key, out T? value);

    /// <summary>异步读取缓存；未命中返回 default。</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>写入缓存。<paramref name="expiration"/> 为 null 时使用默认过期时间。</summary>
    void Set<T>(string key, T value, TimeSpan? expiration = null);

    /// <summary>
    /// 读取缓存，未命中则执行 <paramref name="factory"/> 回源并写入后返回。
    /// 同一 key 的并发未命中会共享同一次回源（single-flight）。
    /// </summary>
    Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default);

    /// <summary>移除指定 key。</summary>
    void Remove(string key);
}
