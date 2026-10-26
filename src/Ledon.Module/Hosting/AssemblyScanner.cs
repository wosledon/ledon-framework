using System.Reflection;
using Ledon.Module.Abstractions;
using Microsoft.Extensions.DependencyModel;

namespace Ledon.Module.Hosting;

/// <summary>程序集扫描结果：纳入的程序集 + 被跳过的（诊断用，解释"为什么我的模块没被发现"）。</summary>
internal sealed class AssemblyScanResult(
    IReadOnlyList<Assembly> assemblies,
    IReadOnlyList<string> skippedAssemblies)
{
    /// <summary>纳入扫描的业务程序集（按名称排序，结果确定）。</summary>
    public IReadOnlyList<Assembly> Assemblies { get; } = assemblies;

    /// <summary>已评估但未纳入的程序集名（诊断信息）。</summary>
    public IReadOnlyList<string> SkippedAssemblies { get; } = skippedAssemblies;
}

/// <summary>
/// 程序集扫描器（允许列表）。
/// 规则：一个程序集被扫描，当且仅当它**直接引用框架程序集**（Ledon.* 功能包）——
/// 「引用框架 = 选择加入」，基础设施（EF Core、xUnit、TestPlatform……）不引用框架，天然排除。
/// 候选来源两个通道合并：
/// A. 应用的依赖上下文（deps.json）——记录全部声明的依赖，与代码是否使用无关，
///    覆盖"项目引用了模块程序集但代码没直接用其类型"的场景（编译器会删掉未被使用的程序集引用，
///    运行库也不会加载它们，仅靠引用图/已加载集合会双双错过）；
/// B. 当前已加载程序集——覆盖入口不可用的宿主（如 VSTest 测试宿主）。
/// 两个通道都过同一套允许列表与框架排除，结果按程序集名排序保证确定性。
/// </summary>
internal static class AssemblyScanner
{
    /// <summary>
    /// 框架自身的程序集名。引用它们不等于业务程序集，需显式排除。
    /// 新增功能包时在此登记（测试会守住这一点：测试程序集引用的全部 Ledon.* 都不得进扫描集）。
    /// </summary>
    private static readonly HashSet<string> FrameworkAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IModule).Assembly.GetName().Name!,                                  // Ledon.Module
        typeof(Ledon.Core.Events.IEventBus).Assembly.GetName().Name!,              // Ledon.Core
        "Ledon.Web",
        "Ledon.Data",
        "Ledon.Caching",
        "Ledon.Queues",
        "Ledon.Queues.RabbitMQ",
        "Ledon.Queues.Kafka",
        "Ledon.Observability",
        "Ledon.Security",
        "Ledon.Scheduling",
        "Ledon.Resilience",
        "Ledon.SignalR",
        "Ledon.Serilog",
        "Ledon.FluentValidation",
        "Ledon.MultiTenancy",
        "Ledon.Configuration",
        "Ledon.Configuration.Nacos",
        "Ledon.Configuration.Apollo",
        "Ledon.Aspire",
        "Ledon.OpenIddict",
        "Ledon.Testing",
        "Ledon.Grpc",
        "Ledon.Workflow",
        "Ledon.Transactions",
        "Ledon.Locking",
        "Ledon.FeatureManagement",
        "Ledon.BlobStorage",
        "Ledon.Notifications",
        "Ledon.Localization",
        "Ledon.AuditLog",
        "Ledon.Application",
        "Ledon.Admin",
        "Ledon.Notifications.Feishu",
        "Ledon.Protocols",
        "Ledon.Sockets.Client",
        "Ledon.Sockets.Server",
        "Ledon.Sip",
        "Ledon.Sip.Client",
        "Ledon.Sip.Server",
        "Ledon.Streaming",
        "Ledon.Webhooks",
        "Ledon.Notifications.Sms",
        "Ledon.Notifications.Push",
        "Ledon.Ids",
        "Ledon.Data.Seeding",
        "Ledon.Settings",
        "Ledon.Search",
        "Ledon.Security.ExternalLogins",
        "Ledon.MultiTenancy.Management",
        "Ledon.Data.Encryption",
        "Ledon.Testing.Containers",
        "Ledon.Security.ApiKeys",
        "Ledon.Security.TokenStores",
        "Ledon.Quotas",
        "Ledon.Data.MultiTenancy",
        "Ledon.Security.Totp",
        "Ledon.Security.Accounts",
        "Ledon.ApiClients",
        "Ledon.Secrets",
        "Ledon.DataExchange",
        "Ledon.Imaging",
        "Ledon.Search.Sync",
        "Ledon.Data.Migrations",
        "Ledon.Idempotency",
        "Ledon.Security.Authorization",
        "Ledon.ApiVersioning",
    };

    /// <summary>
    /// 依赖上下文（deps.json）声明的程序集缓存。解析一次后进程内不变，
    /// 已加载程序集通道（B/C）仍每次动态枚举，不缓存（测试宿主会陆续加载程序集）。
    /// </summary>
    private static readonly Lock DependencyContextGate = new();
    private static IReadOnlyList<Assembly>? _dependencyContextAssemblies;

    private static IReadOnlyList<Assembly> CachedDependencyContextAssemblies()
    {
        if (_dependencyContextAssemblies is not null)
            return _dependencyContextAssemblies;

        lock (DependencyContextGate)
        {
            _dependencyContextAssemblies ??=
                GetDependencyContextAssemblies(DependencyContext.Default).ToArray();

            return _dependencyContextAssemblies;
        }
    }

    public static AssemblyScanResult ResolveAssemblies(LedonOptions options)
    {
        var candidates = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();

        void Consider(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
                return;

            if (candidates.ContainsKey(name))
                return;

            if (FrameworkAssemblyNames.Contains(name))
            {
                skipped.Add(name);
                return;
            }

            if (ReferencesFramework(assembly))
                candidates[name] = assembly;
            else
                skipped.Add(name);
        }

        // 通道 A：默认依赖上下文（应用 deps.json）——覆盖"引用了但未加载"的程序集
        // （解析结果进程内缓存：deps.json 不会变，而多个功能包都会扫描）
        foreach (var assembly in CachedDependencyContextAssemblies())
            Consider(assembly);

        // 通道 B：已加载程序集——覆盖入口不可用的宿主（VSTest 等）
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!loaded.IsDynamic)
                Consider(loaded);
        }

        // 通道 C：已加载且引用框架的程序集的依赖上下文——覆盖默认上下文指向
        // 宿主入口（而非应用）的场景，如 VSTest（GetEntryAssembly() 是 testhost）
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (loaded.IsDynamic || !ReferencesFramework(loaded))
                continue;

            DependencyContext? context;
            try
            {
                context = DependencyContext.Load(loaded);
            }
            catch
            {
                continue;
            }

            if (context is null)
                continue;

            foreach (var assembly in GetDependencyContextAssemblies(context))
                Consider(assembly);
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

    /// <summary>
    /// 从指定的依赖上下文枚举程序集。deps.json 记录全部声明的项目/包依赖，
    /// 与代码是否使用无关——这是模块程序集不被漏掉的关键
    /// （编译器会删掉未被使用的程序集引用，运行库也不会加载它们）。
    /// </summary>
    private static IEnumerable<Assembly> GetDependencyContextAssemblies(DependencyContext? context)
    {
        if (context is null)
            yield break;

        var names = context.RuntimeLibraries
            .SelectMany(library => library.RuntimeAssemblyGroups)
            .SelectMany(group => group.AssetPaths)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal);

        foreach (var name in names)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(name!);
            }
            catch
            {
                // 无法加载（平台特定/不存在）跳过
                continue;
            }

            yield return assembly;
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
