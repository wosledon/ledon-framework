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

    /// <summary>
    /// 负缓存（防穿透）：回源返回 <c>null</c> 时，按此时长缓存一个"未命中"哨兵，
    /// 让同一 key 的并发请求直接命中哨兵而不打到底层存储。
    /// 默认 30 秒；设为 <see cref="TimeSpan.Zero"/> 关闭。
    /// <para>
    /// 热点 key 过期 + 慢回源时，没有负缓存会让所有并发请求同时穿透到 DB
    /// （缓存击穿）。代价是：写入后最长这么久内新 key 仍读到 null——
    /// 所以只对"读多写少、允许秒级延迟"的 key 开启。
    /// </para>
    /// </summary>
    public TimeSpan NegativeExpiration { get; set; } = TimeSpan.FromSeconds(30);
}
