using System.Reflection;

namespace Ledon.Web.Tests;

/// <summary>
/// 架构规则测试（反射实现，无需额外依赖）：框架内部依赖方向受控，
/// 防止"功能包之间悄悄互相引用"导致解耦承诺腐化。新增功能包时在
/// <see cref="AllowedEdges"/> 登记它依赖的框架程序集。
/// </summary>
public class ArchitectureTests
{
    /// <summary>框架程序集 → 允许依赖的框架程序集（空数组 = 只允许依赖更底层）。</summary>
    private static readonly Dictionary<string, string[]> AllowedEdges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ledon.Core"] = [],                                  // 地基：不依赖任何框架包
        ["Ledon.Module"] = ["Ledon.Core"],
        ["Ledon.Data"] = ["Ledon.Module", "Ledon.Core"],
        ["Ledon.Caching"] = ["Ledon.Module"],
        ["Ledon.Configuration"] = ["Ledon.Module"],
        ["Ledon.FluentValidation"] = ["Ledon.Module"],
        ["Ledon.MultiTenancy"] = ["Ledon.Module"],
        ["Ledon.Observability"] = ["Ledon.Module"],
        ["Ledon.Queues"] = ["Ledon.Module"],
        ["Ledon.Resilience"] = ["Ledon.Module"],
        ["Ledon.Scheduling"] = ["Ledon.Module"],
        ["Ledon.Security"] = ["Ledon.Module"],
        ["Ledon.SignalR"] = ["Ledon.Module"],
        ["Ledon.Web"] = ["Ledon.Module"],

        // 组合型包：允许依赖被组合的能力包
        ["Ledon.Aspire"] = ["Ledon.Observability", "Ledon.Resilience"],
        ["Ledon.OpenIddict"] = ["Ledon.Security"],
        ["Ledon.Queues.RabbitMQ"] = ["Ledon.Queues"],
        ["Ledon.Queues.Kafka"] = ["Ledon.Queues"],
        ["Ledon.Testing"] = ["Ledon.Module", "Ledon.Web"],
        ["Ledon.Grpc"] = ["Ledon.Module"],
        // 缓存键的租户隔离（ICacheKeyPrefixProvider 实现）
        ["Ledon.MultiTenancy"] = ["Ledon.Caching"],

        // 只依赖 Microsoft.Extensions.* 的轻量接入包
        ["Ledon.Configuration.Nacos"] = [],
        ["Ledon.Configuration.Apollo"] = [],
        ["Ledon.Serilog"] = [],
        // 组合型：统一事务入口依赖数据层 + 队列（发件箱）
        ["Ledon.Transactions"] = ["Ledon.Data", "Ledon.Queues"],
    };

    [Fact]
    public void Framework_packages_only_depend_on_allowed_assemblies()
    {
        // 每个功能包允许：Ledon.Module（运行时）+ Ledon.Core（基元）+ AllowedEdges 登记的额外依赖
        foreach (var (package, extras) in AllowedEdges)
        {
            var assembly = Assembly.Load(package);

            var allowed = new[] { "Ledon.Module", "Ledon.Core" }
                .Concat(extras)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var frameworkReferences = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => name.StartsWith("Ledon.", StringComparison.Ordinal))
                .ToArray();

            foreach (var reference in frameworkReferences)
            {
                Assert.True(
                    allowed.Contains(reference),
                    $"架构规则被破坏：{package} 引用了 {reference}（允许的依赖：{string.Join(", ", allowed.OrderBy(name => name))}）。" +
                    "功能包之间不得互相引用——解耦是编译期强制的。确有必要时在 ArchitectureTests.AllowedEdges 登记。");
            }
        }
    }

    [Fact]
    public void Core_is_the_foundation_without_framework_references()
    {
        var core = typeof(Ledon.Core.Events.IEventBus).Assembly;

        Assert.DoesNotContain(
            core.GetReferencedAssemblies().Select(reference => reference.Name!),
            name => name!.StartsWith("Ledon.", StringComparison.Ordinal));
    }
}
