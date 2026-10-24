using System.Reflection;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Hosting;

/// <summary>发现结果：模块类型列表（显式优先，其余按 程序集BFS序 + 类型全名序，结果确定）+ 参与约定注册的程序集。</summary>
internal sealed class DiscoveryOutcome(IReadOnlyList<Type> moduleTypes, IReadOnlyList<Assembly> assemblies)
{
    public IReadOnlyList<Type> ModuleTypes { get; } = moduleTypes;

    public IReadOnlyList<Assembly> Assemblies { get; } = assemblies;
}

/// <summary>模块发现：合并显式注册与程序集自动扫描，保证顺序确定、结果去重。</summary>
internal static class ModuleDiscovery
{
    public static DiscoveryOutcome Discover(LedonOptions options)
    {
        var scannedAssemblies = AssemblyScanner.ResolveAssemblies(options);

        var types = new List<Type>();
        var seen = new HashSet<Type>();

        void Add(Type type)
        {
            ValidateModuleType(type);
            if (seen.Add(type))
                types.Add(type);
        }

        // 显式注册优先（顺序 = 调用顺序），可注册被标记 DisableAutoDiscovery 的类型
        foreach (var type in options.ExplicitModules)
            Add(type);

        if (options.AutoDiscovery)
        {
            foreach (var assembly in scannedAssemblies)
            {
                foreach (var type in assembly.GetLoadableTypes()
                    .Where(type => type is { IsClass: true, IsAbstract: false }
                        && typeof(IModule).IsAssignableFrom(type)
                        && !IsAutoDiscoveryDisabled(type))
                    .OrderBy(type => type.FullName, StringComparer.Ordinal))
                {
                    Add(type);
                }
            }
        }

        if (types.Count == 0)
            throw new LedonModuleException(
                "未发现任何模块。请确认模块实现了 IModule 且所在程序集可被扫描；" +
                "需要精确控制时用 AddLedon(o => o.AddModule<T>()) 显式注册。");

        // 约定注册的程序集 = 扫描集 ∪ 显式模块所在程序集
        var conventionAssemblies = scannedAssemblies.ToList();
        var assemblyNames = new HashSet<string>(
            conventionAssemblies
                .Select(assembly => assembly.GetName().Name)
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var type in options.ExplicitModules)
        {
            var name = type.Assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name) && assemblyNames.Add(name))
                conventionAssemblies.Add(type.Assembly);
        }

        return new DiscoveryOutcome(types, conventionAssemblies);
    }

    /// <summary>类型自身或其任一外层容器类标记了 <see cref="DisableAutoDiscoveryAttribute"/>。</summary>
    private static bool IsAutoDiscoveryDisabled(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(DisableAutoDiscoveryAttribute), inherit: false))
                return true;
        }

        return false;
    }

    private static void ValidateModuleType(Type type)
    {
        if (!typeof(IModule).IsAssignableFrom(type))
            throw new LedonModuleException($"{type.FullName} 不是 {nameof(IModule)} 的实现，无法作为模块注册。");

        if (!type.IsClass || type.IsAbstract)
            throw new LedonModuleException($"模块 {type.FullName} 必须是非抽象的具体类。");

        if (type.GetConstructor(Type.EmptyTypes) is null)
            throw new LedonModuleException($"模块 {type.FullName} 缺少无参构造函数，无法实例化。");
    }
}
