using Ledon.Core.Events;
using Ledon.Module.Abstractions;
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
    [Fact]
    public async Task Initialize_follows_dependency_order_and_shutdown_reverses()
    {
        Trace.Clear();
        var services = new ServiceCollection();

        // 故意按"反序"注册：发现顺序 User, Data —— 拓扑排序必须把 Data 提到前面
        services.AddLedon(o => o.AddModule<UserModule>().AddModule<DataModule>());
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
        Trace.Clear();
        var services = new ServiceCollection();

        var ex = Assert.Throws<LedonModuleException>(() =>
            services.AddLedon(o => o.AddModule<CycleFixture.A>().AddModule<CycleFixture.B>()));

        Assert.Contains("循环依赖", ex.Message);
        Assert.Contains("A -> B -> A", ex.Message);
    }

    [Fact]
    public void Missing_dependency_fails_with_guidance()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<LedonModuleException>(() =>
            services.AddLedon(o => o.AddModule<OrphanFixture.Orphan>()));

        Assert.Contains("Orphan", ex.Message);
        Assert.Contains("Unregistered", ex.Message);
        Assert.Contains("AddModule", ex.Message);
    }

    [Fact]
    public void ConfigureServices_cannot_access_not_yet_built_services()
    {
        Trace.Clear();
        var services = new ServiceCollection();

        services.AddLedon(o => o.AddModule<GuardedModule>());

        Assert.Equal(new[] { "Guarded:throws" }, Trace.Snapshot());
    }

    [Fact]
    public async Task Init_failure_rolls_back_initialized_modules_in_reverse_and_keeps_original_exception()
    {
        Trace.Clear();
        var services = new ServiceCollection();
        services.AddLedon(o => o
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
        services.AddLedon(o => o.AddModule<DataModule>());
        services.AddLedon(o => o.AddModule<UserModule>()); // 第二次调用被忽略

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IModuleManager>();

        Assert.Single(manager.Modules);
        Assert.Equal("DataModule", manager.Modules[0].Name);
        Assert.Throws<LedonModuleException>(() => manager.GetModule<PublisherModule>());
    }

    [Fact]
    public async Task Assembly_scan_discovers_top_level_modules_only()
    {
        Trace.Clear();
        var services = new ServiceCollection();
        services.AddSingleton<Recorder>();

        // 显式 + 扫描混合：显式在前，扫描按类型名排序，嵌套类型（环/孤儿夹具）不参与扫描
        services.AddLedon(o => o.AddModule<DataModule>().AddAssembly(typeof(UserModule).Assembly));
        await using var provider = services.BuildServiceProvider();
        await provider.InitializeModulesAsync();

        var manager = provider.GetRequiredService<IModuleManager>();
        Assert.Equal(
            new[] { "DataModule", "GuardedModule", "PublisherModule", "UserModule" },
            manager.Modules.Select(module => module.Name).ToArray());
        Assert.All(manager.Modules, module => Assert.Equal(ModuleState.Initialized, module.State));

        // PublisherModule 初始化时已通过事件总线发布
        Assert.Contains("greet:ledon", provider.GetRequiredService<Recorder>().Snapshot());
    }

    [Fact]
    public async Task Events_flow_from_publisher_module_to_handlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Recorder>();
        services.AddLedon(o => o.AddModule<PublisherModule>());
        await using var provider = services.BuildServiceProvider();

        // 手动发布
        await provider.GetRequiredService<IEventBus>().PublishAsync(new GreetEvent("manual"));

        // 模块初始化时发布（注册发生在 ConfigureServices 阶段，与初始化顺序无关）
        await provider.InitializeModulesAsync();

        Assert.Equal(
            new[] { "greet:manual", "greet:ledon" },
            provider.GetRequiredService<Recorder>().Snapshot());
    }

    [Fact]
    public void ScanAssembly_registers_by_convention_and_is_idempotent()
    {
        var services = new ServiceCollection();
        services.ScanAssembly(typeof(ModuleSystemTests).Assembly);
        services.ScanAssembly(typeof(ModuleSystemTests).Assembly); // TryAdd 幂等

        using var provider = services.BuildServiceProvider();

        Assert.IsType<TestUserService>(provider.GetRequiredService<ITestUserService>());
        Assert.Single(provider.GetServices<ITestUserService>());
        Assert.NotNull(provider.GetRequiredService<PlainService>());
        Assert.Contains(provider.GetServices<IEventHandler<GreetEvent>>(), handler => handler is GreetHandler);
    }

    [Fact]
    public async Task Generic_host_starts_and_stops_modules_via_hosted_service()
    {
        Trace.Clear();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLedon(o => o.AddModule<UserModule>().AddModule<DataModule>());
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
}
