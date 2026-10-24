using System.Diagnostics.CodeAnalysis;
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
/// 模块系统入口。日常用法一行零配置：
/// <code>
/// builder.Services.AddLedon();
/// </code>
/// 框架会自动完成：发现模块（入口程序集及其引用）→ 依赖排序 → 约定注入服务与事件处理器
/// → 按拓扑序注册 → 初始化（经宿主或 <c>InitializeModulesAsync</c>）。
/// 重复调用会被忽略（幂等）。
/// </summary>
public static class LedonServiceCollectionExtensions
{
    /// <param name="services">服务集合。</param>
    /// <param name="configure">模块系统逃生门配置（默认自动发现）。</param>
    [RequiresDynamicCode("Ledon 的自动发现与约定注入基于反射（Assembly.GetTypes / Activator.CreateInstance）。NativeAOT 或裁剪发布时，业务程序集与事件/处理器类型必须可在运行时解析（保留在 rd.xml / DynamicDependency 中），或用 AddAssembly/AddModule 显式注册并 DisableAutoDiscovery。")]
    public static IServiceCollection AddLedon(this IServiceCollection services, Action<LedonOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IModuleManager)))
            return services;

        var options = new LedonOptions();
        configure?.Invoke(options);

        var discovery = ModuleDiscovery.Discover(options);
        var descriptors = ModuleGraph.Build(discovery.ModuleTypes);

        foreach (var descriptor in descriptors)
        {
            descriptor.Instance = (IModule)(Activator.CreateInstance(descriptor.ModuleType)
                ?? throw new LedonModuleException($"无法实例化模块 {descriptor.ModuleType.FullName}。"));
        }

        // 约定自动注入先跑：模块自己的 ConfigureServices 后跑，天然获得覆盖权
        var registrations = new List<string>();
        foreach (var assembly in discovery.Assemblies)
            services.ScanAssembly(assembly, options.DefaultServiceLifetime, registrations);

        var manager = new ModuleManager(descriptors, new ModuleDiagnostics(discovery, registrations));

        // 阶段一：在宿主 BuildServiceProvider 之前按拓扑序执行各模块的服务注册
        foreach (var descriptor in descriptors)
        {
            using var activity = Diagnostics.LedonActivity.Modules.StartActivity("Module.ConfigureServices");
            activity?.SetTag("module.name", descriptor.Name);

            try
            {
                descriptor.Instance!.ConfigureServices(services);
                descriptor.State = ModuleState.Registered;
            }
            catch (Exception ex)
            {
                descriptor.State = ModuleState.Failed;
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                throw new LedonModuleException($"模块 {descriptor.Name} 的 ConfigureServices 执行失败。", ex);
            }
        }

        services.AddSingleton<IModuleManager>(manager);
        services.TryAddSingleton<IEventBus, EventHub>();
        services.TryAddSingleton(options.Events);
        services.AddHostedService<LedonHostedService>();

        return services;
    }
}
