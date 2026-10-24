using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Abstractions;

/// <summary>
/// 模块契约。业务模块实现此接口并用 <see cref="DependsOnAttribute"/> 声明依赖即可接入框架。
/// 三个钩子均为默认接口实现，按需覆盖。
/// </summary>
public interface IModule
{
    /// <summary>
    /// 注册阶段：向服务容器添加服务。在宿主 <c>BuildServiceProvider()</c> 之前按拓扑序调用，
    /// 此阶段 <see cref="ModuleContext.Services"/> 尚不可用。
    /// </summary>
    void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }

    /// <summary>
    /// 初始化阶段：宿主构建完成后按拓扑序调用（被依赖的模块先初始化）。
    /// </summary>
    Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// 关闭阶段：宿主停止时按初始化的逆序调用。
    /// </summary>
    Task OnShutdownAsync(ModuleContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
