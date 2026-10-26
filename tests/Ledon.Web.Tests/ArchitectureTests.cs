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
        // 灰度主体取自多租户上下文
        ["Ledon.FeatureManagement"] = ["Ledon.MultiTenancy"],
        ["Ledon.Locking"] = [],
        // 租户路径前缀隔离
        ["Ledon.BlobStorage"] = ["Ledon.MultiTenancy"],
        ["Ledon.Notifications"] = [],
        // 短信渠道复用通知路由
        ["Ledon.Notifications.Sms"] = ["Ledon.Notifications"],
        // 推送渠道复用通知路由
        ["Ledon.Notifications.Push"] = ["Ledon.Notifications"],
        ["Ledon.Localization"] = [],
        // 复用 EF 拦截器机制与 IDbContextOptionsConfiguration
        ["Ledon.AuditLog"] = ["Ledon.Data"],
        // 组合型：应用层管线复用缓存与统一事务入口
        ["Ledon.Application"] = ["Ledon.Caching", "Ledon.Transactions"],
        // 组合型：运维端点聚合队列/调度/工作流状态
        ["Ledon.Admin"] = ["Ledon.Queues", "Ledon.Scheduling", "Ledon.Workflow", "Ledon.Web"],
        // 协议/套接字：通知渠道接入 Web 端点与通知路由
        ["Ledon.Notifications.Feishu"] = ["Ledon.Notifications", "Ledon.Web"],
        ["Ledon.Protocols"] = [],
        ["Ledon.Sockets.Client"] = ["Ledon.Protocols"],
        ["Ledon.Sockets.Server"] = ["Ledon.Protocols", "Ledon.Module"],
        ["Ledon.Sip"] = ["Ledon.Protocols"],
        ["Ledon.Sip.Client"] = ["Ledon.Sip", "Ledon.Sockets.Client", "Ledon.Protocols"],
        // SIP over TCP 的 Content-Length 切帧实现 IFrameCodec（协议层能力）
        ["Ledon.Sip.Server"] = ["Ledon.Sip", "Ledon.Sockets.Server", "Ledon.Protocols"],
        // 流媒体控制面：端点走 Web
        ["Ledon.Streaming"] = ["Ledon.Web"],
        // 出站 Webhook 管理端点走 Web
        ["Ledon.Webhooks"] = ["Ledon.Web"],
        // 分布式 ID 无框架依赖
        ["Ledon.Ids"] = [],
        // 数据播种复用模块扫描
        ["Ledon.Data.Seeding"] = ["Ledon.Module"],
        // 运行时设置无框架依赖
        ["Ledon.Settings"] = [],
        // 搜索无框架依赖
        ["Ledon.Search"] = [],
        // 外部登录复用安全与 Web 端点
        ["Ledon.Security.ExternalLogins"] = ["Ledon.Security", "Ledon.Web"],
        // 租户管理复用多租户与 Web 端点
        ["Ledon.MultiTenancy.Management"] = ["Ledon.MultiTenancy", "Ledon.Web"],
        // 列加密无框架依赖
        ["Ledon.Data.Encryption"] = [],
        // Testcontainers 基建无框架依赖
        ["Ledon.Testing.Containers"] = [],
        // 动态 API Key 复用安全与 Web
        ["Ledon.Security.ApiKeys"] = ["Ledon.Security", "Ledon.Web"],
        // 共享令牌存储复用安全
        ["Ledon.Security.TokenStores"] = ["Ledon.Security"],
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
