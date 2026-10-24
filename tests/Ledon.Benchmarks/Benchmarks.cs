using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Module.Dependency;
using Ledon.Module.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Benchmarks;

/// <summary>
/// 框架启动热路径基准：程序集扫描（含 deps.json 缓存）、约定注入、事件发布、模块生命周期。
/// 运行：<c>./run.ps1 bench</c>（或 <c>dotnet run -c Release --project tests/Ledon.Benchmarks</c>）。
/// </summary>
public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkRunner.Run(typeof(Program).Assembly, config: null, args: args);
}

/// <summary>程序集扫描：功能包各调一次 LedonAssemblies.Scan，这里测单次与连续调用的成本。</summary>
[MemoryDiagnoser]
public class AssemblyScanBenchmark
{
    [Benchmark]
    public int Scan() => LedonAssemblies.Scan().Count;

    [Benchmark]
    public int Scan_Ten_Times()
    {
        var total = 0;
        for (var i = 0; i < 10; i++)
            total += LedonAssemblies.Scan().Count;

        return total;
    }
}

/// <summary>约定注入：把本程序集的类型按约定注册进容器（启动路径）。</summary>
[MemoryDiagnoser]
public class ConventionRegistrationBenchmark
{
    [Benchmark]
    public int ScanAssembly()
    {
        var services = new ServiceCollection();
        services.ScanAssembly(typeof(ConventionRegistrationBenchmark).Assembly);
        return services.Count;
    }
}

/// <summary>事件发布：单订阅方与多订阅方的进程内发布成本。</summary>
[MemoryDiagnoser]
public class EventPublishBenchmark
{
    private IServiceProvider _provider = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLedon(o => o.DisableAutoDiscovery().AddModule<BenchmarkModule>());
        services.AddTransient<IEventHandler<PingEvent>, PingHandler>();
        services.AddTransient<IEventHandler<PingEvent>, SecondPingHandler>();
        _provider = services.BuildServiceProvider();
    }

    [Benchmark]
    public async Task Publish_One_Handler() =>
        await _provider.GetRequiredService<IEventBus>().PublishAsync(new SingleEvent("x"));

    [Benchmark]
    public async Task Publish_Two_Handlers() =>
        await _provider.GetRequiredService<IEventBus>().PublishAsync(new PingEvent("x"));
}

public record PingEvent(string Name);

public record SingleEvent(string Name);

public class PingHandler : IEventHandler<PingEvent>
{
    public Task HandleAsync(PingEvent @event, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public class SecondPingHandler : IEventHandler<PingEvent>
{
    public Task HandleAsync(PingEvent @event, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public class SingleHandler : IEventHandler<SingleEvent>
{
    public Task HandleAsync(SingleEvent @event, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public class BenchmarkModule : IModule;

/// <summary>模块生命周期：拓扑排序 + 初始化 + 关闭的完整成本。</summary>
[MemoryDiagnoser]
public class ModuleLifecycleBenchmark
{
    [Benchmark]
    public async Task Initialize_And_Shutdown()
    {
        var services = new ServiceCollection();
        services.AddLedon(o => o.DisableAutoDiscovery().AddModule<BenchmarkModule>());
        await using var provider = services.BuildServiceProvider();

        await provider.InitializeModulesAsync();
        await provider.ShutdownModulesAsync();
    }

    [Benchmark]
    public int Build_Dependency_Graph() =>
        ModuleGraph.Build([typeof(BenchmarkModule)]).Count;
}
