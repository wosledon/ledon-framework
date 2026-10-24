using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Module.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Tests;

/// <summary>
/// 并行初始化测试：<c>AddLedon(o =&gt; o.ParallelInitialization = true)</c> 后
/// 同一依赖层（互不依赖）的模块并发初始化，层间仍保持拓扑序；默认关闭时行为与之前一致。
/// </summary>
public class ParallelInitializationTests
{
    [Fact]
    public async Task Independent_modules_initialize_concurrently_when_enabled()
    {
        Trace.Clear();

        var services = new ServiceCollection();
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<SlowModuleA>()
            .AddModule<SlowModuleB>()
            .ParallelInitialization = true);

        await using var provider = services.BuildServiceProvider();

        var elapsed = await Timed.InitializeAsync(provider);

        // 两个各睡 400ms 的独立模块：串行约 800ms，并行应远小于此
        Assert.True(elapsed < TimeSpan.FromMilliseconds(650), $"并行初始化未生效：耗时 {elapsed.TotalMilliseconds:F0}ms");
        Assert.Contains("A:Init", Trace.Snapshot());
        Assert.Contains("B:Init", Trace.Snapshot());
    }

    [Fact]
    public async Task Modules_initialize_sequentially_by_default()
    {
        Trace.Clear();

        var services = new ServiceCollection();
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<SlowModuleA>()
            .AddModule<SlowModuleB>());

        await using var provider = services.BuildServiceProvider();

        var elapsed = await Timed.InitializeAsync(provider);

        // 默认串行：两个 400ms 的模块至少 800ms
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(750), $"默认行为被改变：耗时 {elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Parallel_initialization_preserves_dependency_order()
    {
        Trace.Clear();

        var services = new ServiceCollection();
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<SlowModuleA>()      // 独立（第 0 层）
            .AddModule<DependentModule>()  // 依赖 SlowModuleA（第 1 层）
            .ParallelInitialization = true);

        await using var provider = services.BuildServiceProvider();

        await provider.InitializeModulesAsync();

        // 被依赖者必须先于依赖者初始化
        var trace = Trace.Snapshot();
        Assert.True(
            Array.IndexOf(trace, "A:Init") < Array.IndexOf(trace, "Dependent:Init"),
            "依赖顺序在并行初始化下未被保持");
    }

    [Fact]
    public async Task Parallel_initialization_failure_rolls_back_completed_modules()
    {
        Trace.Clear();

        var services = new ServiceCollection();
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<SlowModuleA>()
            .AddModule<FailingModule>()
            .ParallelInitialization = true);

        await using var provider = services.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.InitializeModulesAsync());

        Assert.Equal("parallel-boom", ex.Message);

        // 同层中已成功初始化的模块被回滚关闭（逆序）
        Assert.Contains("A:Shutdown", Trace.Snapshot());
        Assert.DoesNotContain("Failing:Shutdown", Trace.Snapshot());
    }

    private static class Timed
    {
        public static async Task<TimeSpan> InitializeAsync(ServiceProvider provider)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            await provider.InitializeModulesAsync().ConfigureAwait(false);
            started.Stop();
            return started.Elapsed;
        }
    }
}

// ---------- 夹具 ----------

/// <summary>初始化耗时 400ms 的独立模块 A。</summary>
[DisableAutoDiscovery]
public class SlowModuleA : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("A:Init");
        return Task.Delay(400, cancellationToken);
    }

    public Task OnShutdownAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("A:Shutdown");
        return Task.CompletedTask;
    }
}

/// <summary>初始化耗时 400ms 的独立模块 B。</summary>
[DisableAutoDiscovery]
public class SlowModuleB : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("B:Init");
        return Task.Delay(400, cancellationToken);
    }

    public Task OnShutdownAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("B:Shutdown");
        return Task.CompletedTask;
    }
}

/// <summary>依赖 SlowModuleA 的模块（第 1 层）。</summary>
[DependsOn(typeof(SlowModuleA))]
[DisableAutoDiscovery]
public class DependentModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("Dependent:Init");
        return Task.CompletedTask;
    }
}

/// <summary>总是初始化失败的模块。</summary>
[DisableAutoDiscovery]
public class FailingModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("parallel-boom");
}
