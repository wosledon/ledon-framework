namespace Ledon.Caching;

/// <summary>
/// 缓存服务契约。按 key 读写，未命中时用工厂回源并写入。
/// 实现按需选择：进程内内存（<c>AddLedonCaching</c>，single-flight）、
/// 分布式（<c>AddLedonDistributedCaching</c>，Redis）、
/// 混合两级（<c>AddLedonHybridCaching</c>，L1 内存 + L2 分布式）——
/// 调用方代码不变（契约与实现分离，换实现不改业务）。
/// </summary>
public interface ICacheService
{
    /// <summary>尝试读取缓存；未命中返回 false。</summary>
    bool TryGet<T>(string key, out T? value);

    /// <summary>异步读取缓存；未命中返回 default。</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>写入缓存。<paramref name="expiration"/> 为 null 时使用默认过期时间。</summary>
    /// <param name="slidingExpiration">滑动过期（一段时间未访问即失效）；与绝对过期可同时使用。</param>
    void Set<T>(string key, T value, TimeSpan? expiration = null, TimeSpan? slidingExpiration = null);

    /// <summary>异步写入缓存。</summary>
    Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null);

    /// <summary>
    /// 读取缓存，未命中则执行 <paramref name="factory"/> 回源并写入后返回。
    /// 同一 key 的并发未命中会共享同一次回源（single-flight，内存实现）。
    /// </summary>
    Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default,
        TimeSpan? slidingExpiration = null);

    /// <summary>移除指定 key。</summary>
    void Remove(string key);

    /// <summary>异步移除指定 key。</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按前缀移除（如 <c>RemoveByPrefix("users:")</c>）。
    /// 内存实现精确匹配；分布式/混合实现基于 key 索引与 SCAN/标签，语义见各实现注释。
    /// </summary>
    void RemoveByPrefix(string prefix);

    /// <summary>异步按前缀移除。</summary>
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);
}
