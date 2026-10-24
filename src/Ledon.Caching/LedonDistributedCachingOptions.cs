using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Ledon.Caching;

/// <summary>分布式缓存配置。</summary>
public sealed class LedonDistributedCachingOptions
{
    /// <summary>Redis 连接字符串（如 localhost:6379）。为空时使用进程内内存实现（开发/单机）。</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>key 前缀（多应用共享 Redis 时避免冲突），默认 ledon:。</summary>
    public string KeyPrefix { get; set; } = "ledon:";

    /// <summary>默认过期时间，默认 5 分钟。</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>默认滑动过期，默认空 = 不用滑动过期。</summary>
    public TimeSpan? DefaultSlidingExpiration { get; set; }
}
