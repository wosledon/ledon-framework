using Ledon.Module.Hosting;

namespace Ledon.Module.Runtime;

/// <summary>
/// 模块系统启动诊断：扫描了哪些程序集、跳过了哪些（未引用框架）、
/// 约定注册了哪些服务——用于回答"我的模块/服务为什么没生效"。
/// </summary>
internal sealed class ModuleDiagnostics
{
    public ModuleDiagnostics(DiscoveryOutcome discovery, IReadOnlyList<string> conventionRegistrations)
    {
        ScannedAssemblies = discovery.Assemblies
            .Select(assembly => assembly.GetName().Name!)
            .ToArray();
        SkippedAssemblies = discovery.SkippedAssemblies;
        ConventionRegistrations = conventionRegistrations;
    }

    /// <summary>纳入扫描的程序集名（按名排序）。</summary>
    public IReadOnlyList<string> ScannedAssemblies { get; }

    /// <summary>已加载但未纳入的程序集名（未引用框架）。</summary>
    public IReadOnlyList<string> SkippedAssemblies { get; }

    /// <summary>约定注册明细，如 <c>IUserService -&gt; UserService (Scoped)</c>。</summary>
    public IReadOnlyList<string> ConventionRegistrations { get; }
}
