using System.Diagnostics.CodeAnalysis;
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
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的自动发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，定时任务类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonScheduling(
        this IServiceCollection services,
        Action<LedonSchedulingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonSchedulingOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // 执行历史（运维排障 / 手动重放确认）与手动触发
        services.TryAddSingleton<IScheduledTaskExecutionHistory>(new ScheduledTaskExecutionHistory());
        services.TryAddSingleton<ISchedulerInvoker, SchedulerInvoker>();

        // 多实例部署：领导权选举（仅领导实例执行定时任务）
        if (options.LeaderElection == SchedulerLeaderElection.Redis)
        {
            if (string.IsNullOrWhiteSpace(options.RedisConnectionString))
                throw new InvalidOperationException(
                    "AddLedonScheduling: LeaderElection = Redis 时必须配置 RedisConnectionString");

            services.TryAddSingleton<ISchedulerLock, RedisSchedulerLock>();
        }

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
