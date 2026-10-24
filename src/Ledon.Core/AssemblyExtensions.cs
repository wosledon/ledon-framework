using System.Collections.Concurrent;
using System.Reflection;

namespace Ledon;

/// <summary>程序集反射辅助：容错读取类型（忽略无法加载的类型）。供各功能包共用。</summary>
public static class AssemblyExtensions
{
    /// <summary>
    /// 每个程序集的类型清单缓存。程序集加载后类型集合不会变，而框架各功能包
    /// （模块/数据/队列/调度/gRPC……）启动时都会枚举扫描集的类型——缓存把
    /// 多次全量反射枚举收敛为一次。
    /// </summary>
    private static readonly ConcurrentDictionary<Assembly, Type[]> TypeCache = new();

    /// <summary>
    /// 获取程序集中可加载的类型；单个类型加载失败（<see cref="ReflectionTypeLoadException"/>）时
    /// 忽略失败项而不是整体抛出。结果按程序集缓存（重复调用零反射开销）。
    /// </summary>
    public static IReadOnlyList<Type> GetLoadableTypes(this Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return TypeCache.GetOrAdd(assembly, static candidate =>
        {
            try
            {
                return candidate.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>().ToArray();
            }
        });
    }
}
