using System.Reflection;
using Ledon;
using Ledon.Module;
using Ledon.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

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

        return services.AddLedonHealthChecks(null);
    }

    /// <param name="configure">健康检查参数（如 Redis 连接串）。</param>
    public static IServiceCollection AddLedonHealthChecks(
        this IServiceCollection services,
        Action<LedonHealthCheckOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonHealthCheckOptions();
        configure?.Invoke(options);

        var healthChecks = services.AddHealthChecks();

        // Redis 连通性（配了连接串才登记）
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            healthChecks.AddCheck<RedisHealthCheck>("redis");

            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(options.RedisConnectionString!));
        }

        // AddCheck<T> 只有泛型重载——发现的检查类型是运行时的，需反射调用
        var addCheck = typeof(HealthChecksBuilderAddCheckExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method is { Name: nameof(HealthChecksBuilderAddCheckExtensions.AddCheck), IsGenericMethodDefinition: true }
                && method.GetParameters().Length >= 2
                && method.GetParameters()[1].ParameterType == typeof(string))
            .OrderBy(method => method.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("未找到 HealthChecksBuilder.AddCheck<T> 重载。");

        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IHealthCheck).IsAssignableFrom(type)))
            {
                // 具体类型注册：AddCheck 的工厂要从 DI 解析它
                services.TryAddSingleton(type);

                // IHealthCheck 也可解析：用户代码/其它组件可直接注入
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHealthCheck), type));

                // 关键：登记到 HealthCheckServiceOptions——仅注册 IHealthCheck 不会被
                // MapHealthChecks 感知（DefaultHealthCheckService 只认 Registrations）
                var closed = addCheck.MakeGenericMethod(type);
                var arguments = closed.GetParameters()
                    .Select(parameter => (object?)(parameter.Position switch
                    {
                        0 => healthChecks,
                        1 => type.Name,
                        _ => null,
                    }))
                    .ToArray();

                closed.Invoke(null, arguments);
            }
        }

        return services;
    }
}

/// <summary>把健康检查映射到端点。惯例：<c>/health/live</c> 存活（进程活着即可）、<c>/health/ready</c> 就绪（依赖全部就绪）。</summary>
public static class LedonHealthCheckEndpointExtensions
{
    /// <summary>映射健康检查端点。</summary>
    /// <param name="app">Web 应用。</param>
    /// <param name="pattern">路径，默认 /health。</param>
    /// <param name="predicate">过滤条件，如按 tag 区分存活/就绪（默认全部）。</param>
    public static WebApplication MapLedonHealthChecks(
        this WebApplication app,
        string pattern = "/health",
        Func<HealthCheckRegistration, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (predicate is null)
            app.MapHealthChecks(pattern);
        else
            app.MapHealthChecks(pattern, new HealthCheckOptions { Predicate = predicate });

        return app;
    }

    /// <summary>存活探针：进程在跑即 200（供容器/负载均衡器重启判断）。</summary>
    public static WebApplication MapLedonLiveness(this WebApplication app, string pattern = "/health/live") =>
        app.MapLedonHealthChecks(pattern, _ => false);

    /// <summary>就绪探针：全部注册的检查都通过才 200（供流量开关判断）。</summary>
    public static WebApplication MapLedonReadiness(this WebApplication app, string pattern = "/health/ready") =>
        app.MapLedonHealthChecks(pattern, _ => true);
}
