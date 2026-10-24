using Ledon.Serilog;
using Microsoft.AspNetCore.Builder;
using Serilog;
using Serilog.Core;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Serilog 结构化日志入口。日常用法一行（放在 AddLedonWeb 前后均可）：
/// <code>builder.AddLedonSerilog();</code>
/// 规则：
/// <list type="bullet">
///   <item>配置了 <c>Serilog</c> 节（appsettings.json）时按配置装载（sink/级别/富化全在配置里）；</item>
///   <item>没配时退回控制台 sink（开箱可用的默认）；</item>
///   <item>自动富化 FromLogContext 与当前 Activity 的 TraceId/SpanId（日志与链路按 TraceId 互跳）。</item>
/// </list>
/// </summary>
public static class LedonSerilogHostBuilderExtensions
{
    /// <summary>接入 Serilog（见 <see cref="LedonSerilogHostBuilderExtensions"/> 的规则说明）。</summary>
    /// <param name="builder">Web 宿主构建器。</param>
    /// <param name="configure">追加/覆盖任何 Serilog 配置（在默认装配之后应用）。</param>
    public static WebApplicationBuilder AddLedonSerilog(
        this WebApplicationBuilder builder,
        Action<LoggerConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Host.UseSerilog((context, _, configuration) =>
        {
            // 有 Serilog 配置节 → 按配置装载；否则控制台默认 sink
            if (context.Configuration.GetSection("Serilog").GetChildren().Any())
                configuration.ReadFrom.Configuration(context.Configuration);
            else
                configuration.WriteTo.Console();

            configuration
                .Enrich.FromLogContext()
                .Enrich.With<ActivityTraceEnricher>();

            configure?.Invoke(configuration);
        });

        return builder;
    }
}
