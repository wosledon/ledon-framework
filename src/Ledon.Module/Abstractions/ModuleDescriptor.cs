namespace Ledon.Module.Abstractions;

/// <summary>模块在生命周期中的状态。</summary>
public enum ModuleState
{
    /// <summary>已发现，尚未执行服务注册。</summary>
    Discovered,

    /// <summary>已完成 <see cref="IModule.ConfigureServices"/>。</summary>
    Registered,

    /// <summary>已完成初始化钩子。</summary>
    Initialized,

    /// <summary>已完成关闭钩子。</summary>
    Shutdown,

    /// <summary>初始化或关闭时失败。</summary>
    Failed,
}

/// <summary>
/// 模块的运行时描述：类型、依赖、实例与当前状态。由依赖图构建，顺序即初始化顺序。
/// </summary>
public sealed class ModuleDescriptor
{
    internal ModuleDescriptor(Type moduleType, IReadOnlyList<Type> dependencies)
    {
        ModuleType = moduleType;
        Name = moduleType.Name;
        Dependencies = dependencies;
    }

    /// <summary>模块类名（简名）。</summary>
    public string Name { get; }

    /// <summary>模块类型。</summary>
    public Type ModuleType { get; }

    /// <summary>直接依赖的模块类型（来自 <see cref="DependsOnAttribute"/>，已去重）。</summary>
    public IReadOnlyList<Type> Dependencies { get; }

    /// <summary>模块实例，由框架在发现阶段创建。</summary>
    public IModule? Instance { get; internal set; }

    /// <summary>当前生命周期状态。</summary>
    public ModuleState State { get; internal set; } = ModuleState.Discovered;

    public override string ToString() => $"{Name} [{State}]";
}
