using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Abstractions;

/// <summary>
/// 模块契约。业务模块实现此接口即可接入框架，三个钩子均为可选（默认接口实现）。
/// </summary>
/// <remarks>
/// 设计原则（解耦优先）：模块之间不直接引用对方的服务，跨模块通信通过
/// <see cref="Core.Events.IEventBus"/> 发布/订阅事件完成。
/// </remarks>
public interface IModule
{
    /// <summary>
    /// 注册阶段：向服务容器添加服务。在宿主 <c>BuildServiceProvider()</c> 之前按拓扑序调用。
    /// 此阶段拿不到 <see cref="ModuleContext"/>（容器尚未构建）——注册应保持"哑"的，
    /// 需要服务或日志的逻辑请放到 <see cref="OnInitializedAsync"/>。
    /// </summary>
    void ConfigureServices(IServiceCollection services)
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
