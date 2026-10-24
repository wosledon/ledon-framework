using Ledon.Module.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module;

/// <summary>
/// 非通用主机场景下手动驱动模块生命周期的扩展。
/// 通用主机（<c>Host</c> / WebApplication）无需调用——由 <c>LedonHostedService</c> 自动完成。
/// </summary>
public static class LedonServiceProviderExtensions
{
    /// <summary>初始化全部模块（等价于 <see cref="IModuleManager.InitializeAsync"/>）。</summary>
    public static Task InitializeModulesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<IModuleManager>().InitializeAsync(services, cancellationToken);
    }

    /// <summary>关闭全部模块（等价于 <see cref="IModuleManager.ShutdownAsync"/>）。</summary>
    public static Task ShutdownModulesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<IModuleManager>().ShutdownAsync(cancellationToken);
    }
}
