using Ledon.Observability;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 可观测性入口扩展。日常用法一行：
/// <code>builder.Services.AddLedonObservability(o =&gt; o.OtlpEndpoint = new Uri("http://localhost:4317"));</code>
/// 自动接入 ASP.NET Core / HttpClient 插桩与运行时指标；不配端点则读 OTEL_EXPORTER_OTLP_ENDPOINT。
/// </summary>
public static class LedonObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddLedonObservability(
        this IServiceCollection services,
        Action<LedonObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonObservabilityOptions();
        configure?.Invoke(options);

        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName,
                serviceVersion: options.ServiceVersion));

        if (options.EnableTracing)
        {
            builder.WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (options.OtlpEndpoint is null)
                    tracing.AddOtlpExporter();
            });
        }

        if (options.EnableMetrics)
        {
            builder.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (options.OtlpEndpoint is null)
                    metrics.AddOtlpExporter();
            });
        }

        // 显式端点：统一在 builder 上配置（同时覆盖 trace 与 metric）
        if (options.OtlpEndpoint is not null)
            builder.UseOtlpExporter(OtlpExportProtocol.Grpc, options.OtlpEndpoint);

        return services;
    }
}
