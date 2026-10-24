using Ledon.Core.Events;
using Ledon.Module.Abstractions;
using Ledon.Module.Dependency;
using Ledon.Module.Events;
using Ledon.Module.Hosting;
using Ledon.Module.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Module;

/// <summary>
/// 模块系统入口扩展：
/// <code>
/// builder.Services.AddLedon(o =&gt; o.AddAssembly(typeof(UserModule).Assembly));
/// </code>
/// </summary>
public static class LedonServiceCollectionExtensions
{
    /// <summary>
    /// 注册模块系统。执行顺序：发现模块 → 构建依赖图（拓扑排序 + 环检测）→ 实例化 →
    /// 按序执行各模块 <c>ConfigureServices</c> → 注册模块管理器/事件总线/宿主服务。
    /// 重复调用会被忽略（幂等）。
    /// </summary>
    public static IServiceCollection AddLedon(this IServiceCollection services, Action<LedonOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 幂等：已注册过模块管理器则直接返回
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IModuleManager)))
            return services;

        var options = new LedonOptions();
        configure?.Invoke(options);

        var moduleTypes = ModuleDiscovery.Discover(options);
        var descriptors = ModuleGraph.Build(moduleTypes);

        foreach (var descriptor in descriptors)
        {
            descriptor.Instance = (IModule)(Activator.CreateInstance(descriptor.ModuleType)
                ?? throw new LedonModuleException($"无法实例化模块 {descriptor.ModuleType.FullName}。"));
        }

        var manager = new ModuleManager(descriptors);

        // 注册阶段：在宿主 BuildServiceProvider 之前按拓扑序执行各模块的服务注册
        foreach (var descriptor in descriptors)
        {
            try
            {
                descriptor.Instance!.ConfigureServices(services, new ModuleContext(descriptor));
                descriptor.State = ModuleState.Registered;
            }
            catch (Exception ex)
            {
                descriptor.State = ModuleState.Failed;
                throw new LedonModuleException($"模块 {descriptor.Name} 的 ConfigureServices 执行失败。", ex);
            }
        }

        services.AddSingleton<IModuleManager>(manager);
        services.TryAddSingleton<IEventBus, EventHub>();
        services.AddHostedService<LedonHostedService>();

        return services;
    }
}
