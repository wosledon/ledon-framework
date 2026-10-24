using System.Diagnostics.CodeAnalysis;
using Ledon.Observability;
using Ledon.Resilience;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Aspire 编排默认值：把框架各能力按云原生约定一次装齐（替代 Aspire 模板的 ServiceDefaults 样板）。
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.AddLedonServiceDefaults();   // OTel（环境变量驱动）+ 健康检查 + 弹性 + 服务发现
/// var app = builder.Build();
/// app.MapLedonDefaultEndpoints();      // /health/live 与 /health/ready
/// </code>
/// 各项仍是独立的 Ledon 能力——需要微调时直接调对应包的 AddLedonXxx(o => ...) 覆盖默认。
/// </summary>
public static class LedonServiceDefaultsExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的健康检查自动发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，检查类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    /// <summary>
    /// 装配服务默认值：可观测性（OTLP 端点走 OTEL_EXPORTER_OTLP_ENDPOINT）、
    /// 健康检查、HTTP 弹性管道、服务发现（<c>https+服务名://</c> 寻址）。
    /// </summary>
    [RequiresDynamicCode(AotBoundary)]
    public static WebApplicationBuilder AddLedonServiceDefaults(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddLedonObservability();
        builder.Services.AddLedonHealthChecks();
        builder.Services.AddLedonResilience();
        builder.Services.AddServiceDiscovery();

        return builder;
    }

    /// <summary>映射默认端点：<c>/health/live</c>（存活）与 <c>/health/ready</c>（就绪）。</summary>
    public static WebApplication MapLedonDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapLedonLiveness();
        app.MapLedonReadiness();

        return app;
    }
}
