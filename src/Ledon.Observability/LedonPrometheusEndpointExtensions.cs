using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry.Metrics;

namespace Ledon.Observability;

/// <summary>
/// Prometheus 抓取端点（k8s 标准拉取模式）。需先开启导出器：
/// <c>AddLedonObservability(o =&gt; o.UsePrometheusExporter = true)</c>。
/// <code>app.MapLedonPrometheus();   // GET /metrics</code>
/// </summary>
public static class LedonPrometheusEndpointExtensions
{
    /// <summary>映射 Prometheus 抓取端点。</summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <param name="pattern">路径，默认 /metrics。</param>
    public static IEndpointConventionBuilder MapLedonPrometheus(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/metrics")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPrometheusScrapingEndpoint(pattern);
    }
}
