using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledon.WebSample;

/// <summary>健康检查实现即被自动发现并挂到 /health。</summary>
public class SelfHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("示例运行中"));
}
