using System.Reflection;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Hosting;

/// <summary>
/// <c>AddLedon</c> 的配置：指定要扫描的程序集和/或显式注册的模块。
/// 两者都不指定时回退到入口程序集（<c>Assembly.GetEntryAssembly()</c>）。
/// </summary>
public sealed class LedonOptions
{
    internal List<Assembly> Assemblies { get; } = [];

    internal List<Type> ExplicitModules { get; } = [];

    /// <summary>添加要扫描模块的程序集（仅扫描顶层非抽象类，嵌套类型请用 <see cref="AddModule{TModule}"/> 显式注册）。</summary>
    public LedonOptions AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Assemblies.Add(assembly);
        return this;
    }

    /// <summary>添加 <typeparamref name="TModule"/> 所在的程序集到扫描列表。</summary>
    public LedonOptions AddAssembly<TModule>()
        where TModule : IModule => AddAssembly(typeof(TModule).Assembly);

    /// <summary>显式注册一个模块（优先级高于扫描，可注册嵌套/非公开类型）。</summary>
    public LedonOptions AddModule<TModule>()
        where TModule : IModule
    {
        ExplicitModules.Add(typeof(TModule));
        return this;
    }
}
