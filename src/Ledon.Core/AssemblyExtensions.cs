using System.Reflection;

namespace Ledon;

/// <summary>程序集反射辅助：容错读取类型（忽略无法加载的类型）。供各功能包共用。</summary>
public static class AssemblyExtensions
{
    /// <summary>
    /// 获取程序集中可加载的类型；单个类型加载失败（<see cref="ReflectionTypeLoadException"/>）时
    /// 忽略失败项而不是整体抛出。
    /// </summary>
    public static IEnumerable<Type> GetLoadableTypes(this Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
