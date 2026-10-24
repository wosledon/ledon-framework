using Ledon.Core.Events;
using Ledon.Module.Abstractions;

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
}
