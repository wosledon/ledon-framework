using System.Reflection;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Dependency;

/// <summary>
/// 模块依赖图：校验 <see cref="DependsOnAttribute"/> 声明的依赖并做拓扑排序。
/// 输入顺序会被稳定保留（同为就绪状态的模块按发现顺序出队），保证输出确定性。
/// </summary>
public static class ModuleGraph
{
    /// <summary>
    /// 构建依赖图并返回按初始化顺序排列的模块描述。
    /// </summary>
    /// <exception cref="LedonModuleException">
    /// 依赖的模块未被发现、依赖类型不是 <see cref="IModule"/>、或存在循环依赖（消息中带环路径）。
    /// </exception>
    public static IReadOnlyList<ModuleDescriptor> Build(IEnumerable<Type> moduleTypes)
    {
        ArgumentNullException.ThrowIfNull(moduleTypes);

        var types = moduleTypes.Distinct().ToList();
        foreach (var type in types)
        {
            if (!typeof(IModule).IsAssignableFrom(type) || !type.IsClass || type.IsAbstract)
                throw new LedonModuleException($"{type.FullName} 不是有效的模块类型（必须是实现 IModule 的非抽象类）。");
        }

        var dependencies = new Dictionary<Type, List<Type>>(types.Count);
        var descriptors = new List<ModuleDescriptor>(types.Count);

        foreach (var type in types)
        {
            var deps = type.GetCustomAttributes<DependsOnAttribute>(inherit: false)
                .SelectMany(attribute => attribute.ModuleTypes)
                .Distinct()
                .ToList();

            foreach (var dep in deps)
            {
                if (!typeof(IModule).IsAssignableFrom(dep))
                    throw new LedonModuleException(
                        $"模块 {type.Name} 的 [DependsOn] 指向 {dep.FullName}，它不是 {nameof(IModule)} 的实现。");

                if (!types.Contains(dep))
                    throw new LedonModuleException(
                        $"模块 {type.Name} 依赖的模块 {dep.Name} 未被发现。" +
                        $"请用 AddLedon(o => o.AddModule<{dep.Name}>()) 显式注册，或将其所在程序集加入扫描列表。");
            }

            dependencies[type] = deps;
            descriptors.Add(new ModuleDescriptor(type, deps));
        }

        var descriptorByType = descriptors.ToDictionary(descriptor => descriptor.ModuleType);
        var ordered = TopologicalSort(types, dependencies);
        return ordered.Select(type => descriptorByType[type]).ToList();
    }

    /// <summary>Kahn 拓扑排序：就绪节点按输入顺序入队，保证顺序稳定。</summary>
    private static List<Type> TopologicalSort(List<Type> types, Dictionary<Type, List<Type>> dependencies)
    {
        var inDegree = types.ToDictionary(type => type, type => dependencies[type].Count);
        var dependents = types.ToDictionary(type => type, _ => new List<Type>());

        foreach (var type in types)
        {
            foreach (var dep in dependencies[type])
                dependents[dep].Add(type);
        }

        var queue = new Queue<Type>(types.Where(type => inDegree[type] == 0));
        var ordered = new List<Type>(types.Count);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ordered.Add(current);

            foreach (var dependent in dependents[current])
            {
                if (--inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (ordered.Count != types.Count)
        {
            var remaining = types.Where(type => inDegree[type] > 0).ToHashSet();
            var cycle = FindCycle(types, remaining, dependencies);
            throw new LedonModuleException(
                $"检测到模块循环依赖: {string.Join(" -> ", cycle.Select(type => type.Name))}");
        }

        return ordered;
    }

    /// <summary>在未排序完成的子图中找出一条闭合环路径（按输入顺序选起点，结果确定）。</summary>
    private static List<Type> FindCycle(
        List<Type> types,
        HashSet<Type> remaining,
        Dictionary<Type, List<Type>> dependencies)
    {
        var path = new List<Type>();
        var onPath = new HashSet<Type>();
        List<Type>? cycle = null;

        bool Visit(Type node)
        {
            if (cycle is not null)
                return true;

            if (!remaining.Contains(node))
                return false;

            if (onPath.Contains(node))
            {
                var start = path.IndexOf(node);
                cycle = [.. path.Skip(start), node];
                return true;
            }

            onPath.Add(node);
            path.Add(node);

            foreach (var dep in dependencies[node])
            {
                if (Visit(dep))
                    return true;
            }

            onPath.Remove(node);
            path.RemoveAt(path.Count - 1);
            return false;
        }

        foreach (var candidate in types)
        {
            if (remaining.Contains(candidate) && Visit(candidate))
                break;
        }

        return cycle ?? remaining.ToList();
    }
}
