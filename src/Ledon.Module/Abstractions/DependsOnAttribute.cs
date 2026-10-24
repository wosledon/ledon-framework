namespace Ledon.Module.Abstractions;

/// <summary>
/// 声明当前模块依赖的其它模块。被依赖的模块会先完成服务注册与初始化，后于当前模块关闭。
/// </summary>
/// <remarks>可多次使用；依赖的模块必须也被发现（显式注册或所在程序集被扫描），否则启动即失败。</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DependsOnAttribute : Attribute
{
    public DependsOnAttribute(params Type[] moduleTypes)
    {
        if (moduleTypes is null || moduleTypes.Length == 0)
            throw new ArgumentException("必须至少指定一个模块类型。", nameof(moduleTypes));

        ModuleTypes = moduleTypes;
    }

    /// <summary>当前模块所依赖的模块类型。</summary>
    public IReadOnlyList<Type> ModuleTypes { get; }
}
