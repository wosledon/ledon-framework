using System.Reflection;
using Ledon.Module.Abstractions;

namespace Ledon.Module.Hosting;

/// <summary>
/// 程序集扫描器：自动发现"业务程序集"的双通道——
/// A. 入口程序集引用图 BFS（覆盖引用了但尚未加载的程序集）；
/// B. 当前已加载程序集（覆盖入口不可用的宿主，如 VSTest 测试宿主）。
/// 框架自身与常见基础设施前缀会被跳过；无法加载的引用安全忽略。
/// </summary>
internal static class AssemblyScanner
{
    /// <summary>不参与自动扫描的基础设施前缀（用户程序集一般不会以这些开头）。</summary>
    private static readonly string[] FrameworkPrefixes =
    [
        "System", "mscorlib", "netstandard", "Microsoft.Extensions", "Microsoft.AspNetCore",
        "Microsoft.NETCore", "Microsoft.WindowsDesktop", "Microsoft.EntityFrameworkCore",
        "Microsoft.Data", "Microsoft.IdentityModel", "Microsoft.OpenApi", "Microsoft.TestPlatform",
        "Microsoft.VisualStudio", "Microsoft.Win32", "Microsoft.Net", "SQLitePCLRaw", "xunit",
        "testhost", "testplatform", "coverlet", "NuGet", "Humanizer", "ICSharpCode", "Newtonsoft",
        "Serilog", "NLog",
    ];

    /// <summary>框架自身的程序集（按名精确排除——用户模块即使叫 Ledon.Xxx 也不受影响）。</summary>
    private static readonly HashSet<string> FrameworkAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IModule).Assembly.GetName().Name!,   // Ledon.Module
        typeof(Ledon.Core.Events.IEventBus).Assembly.GetName().Name!,   // Ledon.Core
        "Ledon.Web",
        "Ledon.Data",
    };

    /// <summary>汇总最终扫描集：自动发现（可选）+ 显式指定的程序集。</summary>
    public static IReadOnlyList<Assembly> ResolveAssemblies(LedonOptions options)
    {
        var assemblies = new List<Assembly>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                assemblies.Add(assembly);
        }

        if (options.AutoDiscovery)
        {
            // 通道 A：入口程序集引用图 BFS——覆盖"引用了但尚未加载"的程序集（典型控制台应用）
            if (Assembly.GetEntryAssembly() is { } entry)
            {
                foreach (var assembly in WalkReferences(entry))
                    Add(assembly);
            }

            // 通道 B：当前已加载的程序集——覆盖入口不可用的场景（如 VSTest 测试宿主，
            // GetEntryAssembly() 指向 testhost 而非测试程序集）
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsScannable(assembly))
                    Add(assembly);
            }
        }

        foreach (var assembly in options.Assemblies)
            Add(assembly);

        return assemblies;
    }

    private static IEnumerable<Assembly> WalkReferences(Assembly entry)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();
        queue.Enqueue(entry);

        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            if (!IsScannable(assembly))
                continue;

            var name = assembly.GetName().Name!;
            if (!visited.Add(name))
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
                    // 无法加载的引用（如平台特定）直接跳过，不影响自动发现
                }
            }
        }
    }

    private static bool IsScannable(Assembly assembly)
    {
        if (assembly.IsDynamic)
            return false;

        var name = assembly.GetName().Name;
        if (string.IsNullOrEmpty(name))
            return false;

        // 框架自身（模块系统、Core 基元与各功能包）不扫
        if (FrameworkAssemblyNames.Contains(name))
            return false;

        return !FrameworkPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
