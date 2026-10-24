using System.Reflection;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Hosting;

/// <summary>程序集扫描结果：纳入的程序集 + 被跳过的（诊断用，解释"为什么我的模块没被发现"）。</summary>
internal sealed class AssemblyScanResult(
    IReadOnlyList<Assembly> assemblies,
    IReadOnlyList<string> skippedAssemblies)
{
    /// <summary>纳入扫描的业务程序集（按名称排序，结果确定）。</summary>
    public IReadOnlyList<Assembly> Assemblies { get; } = assemblies;

    /// <summary>已加载但未纳入的程序集名（诊断信息：未引用框架）。</summary>
    public IReadOnlyList<string> SkippedAssemblies { get; } = skippedAssemblies;
}

/// <summary>
/// 程序集扫描器（允许列表，非黑名单）。
/// 规则：一个程序集被扫描，当且仅当它直接引用了框架程序集（Ledon.* 功能包）——
/// 即"引用框架 = 选择加入"，基础设施（EF、xUnit、TestPlatform……）不引用框架，天然被排除。
/// 来源两个通道合并：入口程序集引用闭包（覆盖引用了但尚未加载的）+ 已加载程序集（覆盖宿主入口
/// 不可用的场景，如 VSTest）。结果按程序集名排序，保证跨宿主确定性。
/// </summary>
internal static class AssemblyScanner
{
    /// <summary>
    /// 框架自身的程序集名。引用它们不等于业务程序集，需显式排除。
    /// 新增功能包时在此登记（测试会守住这一点）。
    /// </summary>
    private static readonly HashSet<string> FrameworkAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IModule).Assembly.GetName().Name!,                                  // Ledon.Module
        typeof(Ledon.Core.Events.IEventBus).Assembly.GetName().Name!,              // Ledon.Core
        "Ledon.Web",
        "Ledon.Data",
        "Ledon.Caching",
        "Ledon.Queues",
        "Ledon.Observability",
    };

    public static AssemblyScanResult ResolveAssemblies(LedonOptions options)
    {
        var candidates = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        // 通道 A：入口程序集引用闭包 BFS（覆盖"引用了但尚未加载"）
        if (Assembly.GetEntryAssembly() is { } entry)
        {
            foreach (var assembly in WalkReferences(entry))
                candidates[assembly.GetName().Name!] = assembly;
        }

        // 通道 B：已加载程序集（覆盖入口不可用/不引用业务的宿主，如 VSTest 测试宿主）
        var skipped = new List<string>();
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (loaded.IsDynamic)
                continue;

            var name = loaded.GetName().Name;
            if (string.IsNullOrEmpty(name))
                continue;

            if (FrameworkAssemblyNames.Contains(name))
                continue;

            if (ReferencesFramework(loaded))
                candidates[name] = loaded;
            else
                skipped.Add(name);
        }

        // 显式追加（逃生门）：无条件纳入
        foreach (var assembly in options.Assemblies)
        {
            var name = assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name))
                candidates[name] = assembly;
        }

        var ordered = candidates.Values
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .ToList();

        return new AssemblyScanResult(ordered, skipped);
    }

    /// <summary>入口程序集的引用闭包（含入口自身）。</summary>
    private static IEnumerable<Assembly> WalkReferences(Assembly entry)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();
        queue.Enqueue(entry);

        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name) || !visited.Add(name))
                continue;

            yield return assembly;

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch
                {
                    // 无法加载的引用（如平台特定）跳过
                }
            }
        }
    }

    /// <summary>允许列表判据：直接引用任一框架程序集。</summary>
    private static bool ReferencesFramework(Assembly assembly)
    {
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name is not null && FrameworkAssemblyNames.Contains(reference.Name))
                return true;
        }

        return false;
    }
}
