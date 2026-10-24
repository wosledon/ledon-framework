using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Ledon.Observability;

/// <summary>Redis 连通性健康检查（配了 <c>RedisConnectionString</c> 时由 AddLedonHealthChecks 登记）。</summary>
internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisHealthCheck(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ping = await _multiplexer.GetDatabase().PingAsync().ConfigureAwait(false);
            return HealthCheckResult.Healthy($"Redis 响应 {ping.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis 不可用", ex);
        }
    }
}
