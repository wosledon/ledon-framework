using System.Reflection;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Hosting;

/// <summary>模块发现：合并显式注册与程序集扫描，保证顺序确定、结果去重。</summary>
internal static class ModuleDiscovery
{
    public static IReadOnlyList<Type> Discover(LedonOptions options)
    {
        if (options.Assemblies.Count == 0 && options.ExplicitModules.Count == 0)
        {
            var entryAssembly = Assembly.GetEntryAssembly()
                ?? throw new LedonModuleException(
                    "未指定模块程序集，且当前环境无法获取入口程序集；" +
                    "请使用 AddLedon(o => o.AddAssembly(...)) 显式指定。");

            options.Assemblies.Add(entryAssembly);
        }

        var types = new List<Type>();
        var seen = new HashSet<Type>();

        void Add(Type type)
        {
            ValidateModuleType(type);
            if (seen.Add(type))
                types.Add(type);
        }

        // 显式注册优先（顺序 = 调用顺序），扫描结果按类型全名排序保证确定性
        foreach (var type in options.ExplicitModules)
            Add(type);

        foreach (var assembly in options.Assemblies)
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false, DeclaringType: null }
                    && typeof(IModule).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                Add(type);
            }
        }

        if (types.Count == 0)
            throw new LedonModuleException("未发现任何模块。请检查 AddLedon 的程序集配置或模块是否实现了 IModule。");

        return types;
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
