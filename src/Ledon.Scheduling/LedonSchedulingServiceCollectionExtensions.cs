using Ledon;
using Ledon.Module;
using Ledon.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 定时任务入口。日常用法一行：
/// <code>builder.Services.AddLedonScheduling();</code>
/// 之后任何 <see cref="IScheduledTask"/> 实现（在被扫描的程序集里）都会按 cron 自动执行。
/// </summary>
public static class LedonSchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddLedonScheduling(
        this IServiceCollection services,
        Action<LedonSchedulingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonSchedulingOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // 任务自动发现（沿用框架的允许列表扫描）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IScheduledTask).IsAssignableFrom(type)))
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IScheduledTask), type));
            }
        }

        services.AddHostedService<SchedulerHostedService>();

        return services;
    }
}
