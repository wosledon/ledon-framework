namespace Ledon.Observability;

/// <summary>健康检查参数。</summary>
public sealed class LedonHealthCheckOptions
{
    /// <summary>
    /// Redis 连接串：配置后登记 Redis 连通性检查（名 "redis"，PING 探测）。
    /// 不配则不登记（Redis 缓存本身按需，不该让没装 Redis 的应用就绪失败）。
    /// </summary>
    public string? RedisConnectionString { get; set; }
}
