namespace Ledon.Caching;

/// <summary>缓存默认参数。</summary>
public sealed class LedonCachingOptions
{
    /// <summary>默认过期时间（相对），默认 5 分钟。</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>默认滑动过期（一段时间未访问即失效），默认空 = 不用滑动过期。</summary>
    public TimeSpan? DefaultSlidingExpiration { get; set; }

    /// <summary>所有 key 的前缀（多应用共享缓存时避免冲突），默认空。</summary>
    public string? KeyPrefix { get; set; }
}
