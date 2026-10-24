namespace Ledon.Module.Abstractions;

/// <summary>
/// 模块管理器：对外暴露模块清单与状态，并驱动初始化/关闭两个运行阶段。
/// 由 <c>AddLedon</c> 注册为单例。
/// </summary>
public interface IModuleManager
{
    /// <summary>全部模块，顺序即拓扑初始化顺序。</summary>
    IReadOnlyList<ModuleDescriptor> Modules { get; }

    /// <summary>是否已完成初始化。</summary>
    bool IsInitialized { get; }

    /// <summary>按类型获取模块描述，未发现时抛 <see cref="LedonModuleException"/>。</summary>
    ModuleDescriptor GetModule<TModule>()
        where TModule : IModule;

    /// <inheritdoc cref="GetModule{TModule}"/>
    ModuleDescriptor GetModule(Type moduleType);

    /// <summary>
    /// 初始化全部模块：按拓扑序依次调用各模块的 <see cref="IModule.OnInitializedAsync"/>。
    /// 任一模块失败时将已初始化模块逆序回滚，并抛出原始异常。
    /// </summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default);

    /// <summary>
    /// 关闭全部模块：按初始化逆序调用 <see cref="IModule.OnShutdownAsync"/>。
    /// 会尽量完成所有模块的关闭，若存在失败则最后抛出异常。
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
