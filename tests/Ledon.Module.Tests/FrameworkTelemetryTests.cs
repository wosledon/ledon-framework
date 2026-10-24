using System.Diagnostics;
using Ledon.Module.Abstractions;
using Ledon.Module.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Tests;
/// <summary>
/// 框架自观测：模块生命周期 / 事件发布必须产生 span（ActivityListener 捕获）。
/// 接入 OpenTelemetry 后这些 span 自动进入链路树——"框架帮你做了什么"全程可见。
/// </summary>
public class FrameworkTelemetryTests
{
    private static IReadOnlyList<Activity> Capture(Action pipeline)
    {
        var activities = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Ledon", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };

        ActivitySource.AddActivityListener(listener);
        pipeline();
        return activities;
    }

    [Fact]
    public async Task Module_lifecycle_emits_spans_in_topological_order()
    {
        IReadOnlyList<Activity> activities = [];

        await Task.Run(() =>
        {
            activities = Capture(() =>
            {
                var services = new ServiceCollection();
                services.AddLedon(options => options
                    .DisableAutoDiscovery()
                    .AddModule<UserModule>()      // 依赖 DataModule——反序注册
                    .AddModule<DataModule>());

                using var provider = services.BuildServiceProvider();
                provider.InitializeModulesAsync().GetAwaiter().GetResult();
                provider.ShutdownModulesAsync().GetAwaiter().GetResult();
            });
        });

        var operationNames = activities.Select(activity => activity.OperationName).ToArray();
        Assert.Contains("Module.ConfigureServices", operationNames);
        Assert.Contains("Module.Initialize", operationNames);
        Assert.Contains("Module.Shutdown", operationNames);

        // 拓扑序：被依赖者先初始化、后关闭
        Assert.True(IndexOf("Module.Initialize", "DataModule") < IndexOf("Module.Initialize", "UserModule"),
            "DataModule 应先于 UserModule 初始化");
        Assert.True(IndexOf("Module.Shutdown", "UserModule") < IndexOf("Module.Shutdown", "DataModule"),
            "UserModule 应先于 DataModule 关闭");

        // ConfigureServices 阶段的 span 带模块名
        Assert.Contains(activities, activity =>
            activity.OperationName == "Module.ConfigureServices"
            && activity.GetTagItem("module.name") is "DataModule");

        int IndexOf(string operation, string moduleName) =>
            activities.ToList().FindIndex(activity =>
                activity.OperationName == operation
                && activity.GetTagItem("module.name") is string name && name == moduleName);
    }

    [Fact]
    public async Task Event_publish_emits_span_with_event_type()
    {
        IReadOnlyList<Activity> activities = [];

        await Task.Run(() =>
        {
            activities = Capture(() =>
            {
                var services = new ServiceCollection();
                services.AddSingleton<Recorder>();   // GreetHandler 的依赖
                services.AddLedon(options => options
                    .DisableAutoDiscovery()
                    .AddModule<PublisherModule>());  // 初始化时发布 GreetEvent

                using var provider = services.BuildServiceProvider();
                provider.InitializeModulesAsync().GetAwaiter().GetResult();
            });
        });

        var publish = Assert.Single(activities, activity => activity.OperationName == "Event.Publish");
        Assert.Equal("Ledon.Module.Tests.Fixtures.GreetEvent", publish.GetTagItem("event.type"));
    }

    [Fact]
    public async Task Module_can_start_custom_spans_via_context()
    {
        IReadOnlyList<Activity> activities = [];

        await Task.Run(() =>
        {
            activities = Capture(() =>
            {
                var services = new ServiceCollection();
                services.AddLedon(options => options
                    .DisableAutoDiscovery()
                    .AddModule<SpanModule>());

                using var provider = services.BuildServiceProvider();
                provider.InitializeModulesAsync().GetAwaiter().GetResult();
            });
        });

        // 模块经 ModuleContext.StartActivity 发出的自定义 span（挂在模块初始化 span 之下）
        Assert.Contains(activities, activity => activity.OperationName == "SpanModule.DoWork");
    }
}

/// <summary>验证模块经 <see cref="ModuleContext.StartActivity"/> 发自定义 span。</summary>
[DisableAutoDiscovery]
public class SpanModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        using var activity = context.StartActivity("DoWork");
        activity?.SetTag("demo", true);
        return Task.CompletedTask;
    }
}
