using Ledon;
using Ledon.Module;
using Ledon.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 健康检查：自动发现扫描到的程序集中的 <see cref="IHealthCheck"/> 实现并注册。
/// 日常用法：
/// <code>
/// builder.Services.AddLedonHealthChecks();
/// app.MapLedonHealthChecks();   // GET /health
/// </code>
/// </summary>
public static class LedonHealthCheckServiceCollectionExtensions
{
    public static IServiceCollection AddLedonHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHealthChecks();

        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IHealthCheck).IsAssignableFrom(type)))
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHealthCheck), type));
            }
        }

        return services;
    }
}

/// <summary>把健康检查映射到端点（默认 /health）。</summary>
public static class LedonHealthCheckEndpointExtensions
{
    public static WebApplication MapLedonHealthChecks(this WebApplication app, string pattern = "/health")
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(pattern);
        return app;
    }
}
