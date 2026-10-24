using Ledon.Module.Diagnostics;
using Ledon.Observability;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 可观测性入口扩展。日常用法一行：
/// <code>builder.Services.AddLedonObservability(o =&gt; o.OtlpEndpoint = new Uri("http://localhost:4317"));</code>
/// 自动接入 ASP.NET Core / HttpClient / 运行时插桩与框架自身源
/// （<see cref="LedonActivity.RootSourceName"/>：模块生命周期 / 事件发布 / 队列处理的 span）；
/// 不配 OTLP 端点则读 OTEL_EXPORTER_OTLP_ENDPOINT 环境变量。
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
                    .AddSource(LedonActivity.RootSourceName)
                    .AddSource($"{LedonActivity.RootSourceName}.*")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (options.UseConsoleExporter)
                    tracing.AddConsoleExporter();

                if (options.OtlpEndpoint is null)
                    tracing.AddOtlpExporter();
            });
        }

        if (options.EnableMetrics)
        {
            builder.WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(LedonActivity.RootSourceName)
                    .AddMeter($"{LedonActivity.RootSourceName}.*")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (options.UseConsoleExporter)
                    metrics.AddConsoleExporter();

                if (options.OtlpEndpoint is null)
                    metrics.AddOtlpExporter();

                // Prometheus 拉取模式：配合 app.MapLedonPrometheus() 暴露 /metrics
                if (options.UsePrometheusExporter)
                    metrics.AddPrometheusExporter();
            });
        }

        if (options.EnableLogs)
        {
            services.AddLogging(logging => logging.AddOpenTelemetry(openTelemetry =>
            {
                openTelemetry.IncludeFormattedMessage = true;
                openTelemetry.IncludeScopes = true;   // 关联 ID 等日志作用域随日志导出

                if (options.UseConsoleExporter)
                    openTelemetry.AddConsoleExporter();

                // 日志经 ILoggingBuilder 注册（不在 OTel builder 上），端点需单独配置：
                // 显式端点优先，否则走 OTEL_EXPORTER_OTLP_ENDPOINT
                if (options.OtlpEndpoint is not null)
                    openTelemetry.AddOtlpExporter(exporter => exporter.Endpoint = options.OtlpEndpoint);
                else
                    openTelemetry.AddOtlpExporter();
            }));
        }

        // 显式端点：统一在 builder 上配置（同时覆盖 trace 与 metric）
        if (options.OtlpEndpoint is not null)
            builder.UseOtlpExporter(OtlpExportProtocol.Grpc, options.OtlpEndpoint);

        return services;
    }
}
