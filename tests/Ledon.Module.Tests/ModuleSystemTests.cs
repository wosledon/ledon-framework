using Ledon.Core.Events;
using Ledon.Module.Abstractions;
using Ledon.Module.Dependency;
using Ledon.Module.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Module.Tests;

/// <summary>
/// 模块系统核心行为测试。测试程序集已禁用并行（见 AssemblyInfo.cs），
/// 每个用例开头 Clear 轨迹，断言钉住完整顺序而不只是"包含"。
/// </summary>
public class ModuleSystemTests
{
    // ---------- 零配置自动发现 / 自动注入 ----------

    [Fact]
    public async Task Zero_config_discovers_modules_and_auto_wires_services_and_events()
    {
        Trace.Clear();
        var services = new ServiceCollection();
        services.AddSingleton<Recorder>();

        // 零配置：不指定任何程序集，入口程序集（本测试程序集）自动发现
        // CycleFixture / OrphanFixture / RollbackFixture 标了 DisableAutoDiscovery，
        // 能被自动发现（没抛循环依赖异常）本身就证明了逃生门生效
        services.AddLedon();
        await using var provider = services.BuildServiceProvider();

        // 约定自动注入：IName ↔ Name 生效
        Assert.IsType<TestUserService>(provider.GetRequiredService<ITestUserService>());

        // 无接口类（DTO 类）不会被自动注册——防止把 MVC 动作参数类型注入 DI
        // 而破坏 [ApiController] 的隐式 [FromBody] 推断（详见 ConventionScanner 注释）
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PlainService>());

        await provider.InitializeModulesAsync();

        var manager = provider.GetRequiredService<IModuleManager>();
        Assert.Equal(
            new[] { "DataModule", "PublisherModule", "UserModule" },
            manager.Modules.Select(module => module.Name).ToArray());
        Assert.Equal(
            new[]
            {
                "Data:Configure", "Publisher:Configure", "User:Configure",
                "Data:Init", "Publisher:Init", "User:Init",
            },
            Trace.Snapshot());

        // PublisherModule 初始化时发布 GreetEvent：处理器没有任何手动注册，被自动消费
        Assert.Equal(new[] { "greet:ledon" }, provider.GetRequiredService<Recorder>().Snapshot());
    }

    [Fact]
    public async Task Events_flow_from_publisher_module_to_handlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Recorder>();
        services.AddLedon(o => o.DisableAutoDiscovery().AddModule<PublisherModule>());
        await using var provider = services.BuildServiceProvider();

        // 手动发布
        await provider.GetRequiredService<IEventBus>().PublishAsync(new GreetEvent("manual"));

        // 模块初始化时发布（处理器经约定自动注册，与初始化顺序无关）
        await provider.InitializeModulesAsync();

        Assert.Equal(
            new[] { "greet:manual", "greet:ledon" },
            provider.GetRequiredService<Recorder>().Snapshot());
    }

    // ---------- 显式模式：生命周期与依赖 ----------

    [Fact]
    public async Task Initialize_follows_dependency_order_and_shutdown_reverses()
    {
        Trace.Clear();
        var services = new ServiceCollection();

        // 显式模式：故意按"反序"注册——发现顺序 User, Data，拓扑排序必须把 Data 提到前面
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<UserModule>()
            .AddModule<DataModule>());
        await using var provider = services.BuildServiceProvider();

        await provider.InitializeModulesAsync();
        Assert.Equal(
            new[] { "Data:Configure", "User:Configure", "Data:Init", "User:Init" },
            Trace.Snapshot());

        await provider.ShutdownModulesAsync();
        Assert.Equal(
            new[]
            {
                "Data:Configure", "User:Configure", "Data:Init", "User:Init",
                "User:Shutdown", "Data:Shutdown",
            },
            Trace.Snapshot());

        var manager = provider.GetRequiredService<IModuleManager>();
        Assert.False(manager.IsInitialized);
        Assert.Equal(ModuleState.Shutdown, manager.GetModule<DataModule>().State);
        Assert.Equal(ModuleState.Shutdown, manager.GetModule<UserModule>().State);
        Assert.Equal(new[] { typeof(DataModule) }, manager.GetModule<UserModule>().Dependencies);
    }

    [Fact]
    public void Cycle_dependency_fails_fast_with_path()
    {
        // 依赖图是纯函数，直接单元测试，不必绕道 AddLedon
        var ex = Assert.Throws<LedonModuleException>(() => ModuleGraph.Build(
            [typeof(CycleFixture.A), typeof(CycleFixture.B)]));

        Assert.Contains("循环依赖", ex.Message);
        Assert.Contains("A -> B -> A", ex.Message);
    }

    [Fact]
    public void Missing_dependency_fails_with_guidance()
    {
        var ex = Assert.Throws<LedonModuleException>(() => ModuleGraph.Build(
            [typeof(OrphanFixture.Orphan)]));

        Assert.Contains("Orphan", ex.Message);
        Assert.Contains("Unregistered", ex.Message);
        Assert.Contains("AddModule", ex.Message);
    }

    [Fact]
    public async Task Init_failure_rolls_back_initialized_modules_in_reverse_and_keeps_original_exception()
    {
        Trace.Clear();
        var services = new ServiceCollection();
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<RollbackFixture.First>()
            .AddModule<RollbackFixture.Second>()
            .AddModule<RollbackFixture.Failer>());
        await using var provider = services.BuildServiceProvider();

        // 原始异常必须原样抛出，不被包装掩盖
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.InitializeModulesAsync());
        Assert.Equal("boom", ex.Message);

        // 回滚顺序必须是已初始化模块的逆序：Second 先于 First 关闭，Failer 从未初始化所以不关
        Assert.Equal(
            new[]
            {
                "First:Configure", "Second:Configure", "Failer:Configure",
                "First:Init", "Second:Init", "Failer:Init",
                "Second:Shutdown", "First:Shutdown",
            },
            Trace.Snapshot());

        var manager = provider.GetRequiredService<IModuleManager>();
        Assert.False(manager.IsInitialized);
        Assert.Equal(ModuleState.Shutdown, manager.GetModule<RollbackFixture.First>().State);
        Assert.Equal(ModuleState.Shutdown, manager.GetModule<RollbackFixture.Second>().State);
        Assert.Equal(ModuleState.Failed, manager.GetModule<RollbackFixture.Failer>().State);
    }

    [Fact]
    public void AddLedon_is_idempotent_and_rejects_unknown_module_lookup()
    {
        var services = new ServiceCollection();
        services.AddLedon(o => o.DisableAutoDiscovery().AddModule<DataModule>());
        services.AddLedon(o => o.DisableAutoDiscovery().AddModule<UserModule>()); // 第二次调用被忽略

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IModuleManager>();

        Assert.Single(manager.Modules);
        Assert.Equal("DataModule", manager.Modules[0].Name);
        Assert.Throws<LedonModuleException>(() => manager.GetModule<PublisherModule>());
    }

    [Fact]
    public async Task Generic_host_starts_and_stops_modules_via_hosted_service()
    {
        Trace.Clear();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<UserModule>()
            .AddModule<DataModule>());
        using var host = builder.Build();

        await host.StartAsync();
        Assert.Equal(
            new[] { "Data:Configure", "User:Configure", "Data:Init", "User:Init" },
            Trace.Snapshot());

        await host.StopAsync();
        Assert.Equal(
            new[]
            {
                "Data:Configure", "User:Configure", "Data:Init", "User:Init",
                "User:Shutdown", "Data:Shutdown",
            },
            Trace.Snapshot());

        var manager = host.Services.GetRequiredService<IModuleManager>();
        Assert.False(manager.IsInitialized);
        Assert.Equal(ModuleState.Shutdown, manager.GetModule<DataModule>().State);
        Assert.Equal(ModuleState.Shutdown, manager.GetModule<UserModule>().State);
    }

    // ---------- 逃生门：手动扫描 API 仍然可用 ----------

    [Fact]
    public void ScanAssembly_manual_still_works()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Recorder>();   // 测试自有的基础设施服务，显式注册
        services.ScanAssembly(typeof(ModuleSystemTests).Assembly);
        services.ScanAssembly(typeof(ModuleSystemTests).Assembly); // TryAdd 幂等

        using var provider = services.BuildServiceProvider();

        Assert.IsType<TestUserService>(provider.GetRequiredService<ITestUserService>());
        Assert.Single(provider.GetServices<ITestUserService>());

        // 无接口类不再自注册（防 [ApiController] 推断失效）
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PlainService>());
        Assert.Contains(provider.GetServices<IEventHandler<GreetEvent>>(), handler => handler is GreetHandler);
    }
}
