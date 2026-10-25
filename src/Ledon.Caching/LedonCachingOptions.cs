namespace Ledon.Caching;

/// <summary>缓存默认参数。</summary>
public sealed class LedonCachingOptions
{
    /// <summary>默认过期时间（相对），默认 5 分钟。</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>默认滑动过期（一段时间未访问即失效），默认空 = 不用滑动过期。</summary>
    public TimeSpan? DefaultSlidingExpiration { get; set; }

    /// <summary>
    /// 所有 key 的前缀（多应用共享缓存时避免冲突），默认空。
    /// 多租户隔离请实现 <see cref="ICacheKeyPrefixProvider"/>（按租户动态前缀），
    /// 而不是把这个静态前缀当租户隔离手段——它是进程级常量，所有租户共用。
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// 跨实例失效（多实例部署的 L1 一致性）：本实例删除键/前缀时经 Redis 发布订阅广播，
    /// 其它实例同步失效。默认 <c>false</c>（单实例部署不需要）。
    /// </summary>
    public bool CrossInstanceInvalidation { get; set; }

    /// <summary>跨实例失效用的 Redis 连接串（<see cref="CrossInstanceInvalidation"/> 为 true 时必填）。</summary>
    public string? RedisConnectionString { get; set; }
}
