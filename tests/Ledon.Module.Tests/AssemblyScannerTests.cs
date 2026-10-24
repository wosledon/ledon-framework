using System.Reflection;
using Ledon.Core.Events;
using Ledon.Module.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;

namespace Ledon.Module.Tests;

/// <summary>
/// 程序集扫描器（允许列表）行为测试。规则：引用框架的程序集才被扫描；
/// 框架自身与不引用框架的基础设施（xUnit、EF……）一律排除。
/// </summary>
public class AssemblyScannerTests
{
    [Fact]
    public void Scan_set_includes_only_framework_referencing_assemblies()
    {
        var names = LedonAssemblies.Scan()
            .Select(assembly => assembly.GetName().Name!)
            .ToHashSet(StringComparer.Ordinal);

        // 引用框架的测试程序集被纳入
        Assert.Contains(typeof(AssemblyScannerTests).Assembly.GetName().Name!, names);

        // 框架自身不扫
        Assert.DoesNotContain(typeof(IModule).Assembly.GetName().Name!, names);
        Assert.DoesNotContain(typeof(IEventBus).Assembly.GetName().Name!, names);

        // 不引用框架的基础设施不扫（xunit 在测试进程中已加载）
        Assert.DoesNotContain(typeof(Assert).Assembly.GetName().Name!, names);
    }

    [Fact]
    public void Scan_result_is_deterministically_ordered()
    {
        var names = LedonAssemblies.Scan()
            .Select(assembly => assembly.GetName().Name!)
            .ToArray();

        var sorted = names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void AddAssembly_escape_hatch_forces_inclusion()
    {
        // System.Text.Json 不引用框架，默认不在扫描集；显式追加后必须纳入
        var forced = typeof(System.Text.Json.JsonSerializer).Assembly;

        var names = LedonAssemblies.Scan(options => options.AddAssembly(forced))
            .Select(assembly => assembly.GetName().Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(forced.GetName().Name!, names);
    }

    // ---------- 回归：引用了但代码未使用的模块程序集 ----------
    // 真实应用常见形态：项目引用了模块程序集，但入口代码只依赖 IModule 约定、
    // 从不直接使用模块类型。此时编译器会删掉该程序集引用，运行库也不会加载它，
    // 仅靠"入口引用图 / 已加载集合"会双双错过——必须经 deps.json 通道发现。

    [Fact]
    public void Dependency_context_keeps_unused_project_references()
    {
        // 前提 1：deps.json 记录全部声明的依赖（与代码使用无关）
        var context = DependencyContext.Load(typeof(AssemblyScannerTests).Assembly)!;
        var dependencyNames = context.RuntimeLibraries
            .SelectMany(library => library.RuntimeAssemblyGroups)
            .SelectMany(group => group.AssetPaths)
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Ledon.TestModules", dependencyNames);

        // 前提 2：编译后的程序集引用里确实没有它（引用被消除了，否则本回归无意义）
        var compiledReferences = typeof(AssemblyScannerTests).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Ledon.TestModules", compiledReferences);
    }

    [Fact]
    public void Module_in_unused_referenced_assembly_is_discovered()
    {
        var services = new ServiceCollection();
        services.AddLedon();   // 零配置自动发现（VSTest 下入口是 testhost，走已加载程序集的依赖上下文通道）

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IModuleManager>();

        Assert.Contains("ExternalModule", manager.Modules.Select(module => module.Name));
    }
}
