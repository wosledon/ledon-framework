using Ledon.Core.Events;
using Ledon.Module.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Tests.Fixtures;

/// <summary>静态执行轨迹：记录各模块钩子的调用顺序（测试已禁用并行，避免互相干扰）。</summary>
public static class Trace
{
    private static readonly List<string> Entries = [];

    public static void Add(string entry)
    {
        lock (Entries)
            Entries.Add(entry);
    }

    public static string[] Snapshot()
    {
        lock (Entries)
            return Entries.ToArray();
    }

    public static void Clear()
    {
        lock (Entries)
            Entries.Clear();
    }
}

/// <summary>由测试容器持有、跨模块共享的运行时记录器。</summary>
public sealed class Recorder
{
    private readonly List<string> _entries = [];

    public void Add(string entry)
    {
        lock (_entries)
            _entries.Add(entry);
    }

    public string[] Snapshot()
    {
        lock (_entries)
            return _entries.ToArray();
    }
}

// ---------- 参与自动发现的正常模块 ----------

/// <summary>依赖 DataModule 的业务模块。</summary>
[DependsOn(typeof(DataModule))]
public class UserModule : IModule
{
    public void ConfigureServices(IServiceCollection services) =>
        Trace.Add("User:Configure");

    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("User:Init");
        return Task.CompletedTask;
    }

    public Task OnShutdownAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("User:Shutdown");
        return Task.CompletedTask;
    }
}

/// <summary>被 UserModule 依赖的底层模块，应先注册、先初始化、后关闭。</summary>
public class DataModule : IModule
{
    public void ConfigureServices(IServiceCollection services) =>
        Trace.Add("Data:Configure");

    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("Data:Init");
        return Task.CompletedTask;
    }

    public Task OnShutdownAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("Data:Shutdown");
        return Task.CompletedTask;
    }
}

// ---------- 事件 ----------

public record GreetEvent(string Name);

/// <summary>事件处理器：无需任何手动注册，约定扫描会自动注入容器。</summary>
public class GreetHandler : IEventHandler<GreetEvent>
{
    private readonly Recorder _recorder;

    public GreetHandler(Recorder recorder) => _recorder = recorder;

    public Task HandleAsync(GreetEvent @event, CancellationToken cancellationToken = default)
    {
        _recorder.Add($"greet:{@event.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>初始化时通过事件总线发布事件——发布方不需要知道谁在处理。</summary>
public class PublisherModule : IModule
{
    public void ConfigureServices(IServiceCollection services) =>
        Trace.Add("Publisher:Configure");

    public async Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("Publisher:Init");
        await context.Events.PublishAsync(new GreetEvent("ledon"), cancellationToken);
    }
}

// ---------- 约定注册夹具 ----------

public interface ITestUserService
{
    string Name { get; }
}

public class TestUserService : ITestUserService
{
    public string Name => "svc";
}

public class PlainService;

// ---------- 逃生门夹具：标记 DisableAutoDiscovery，只能显式 AddModule 注册 ----------
// （不标记的话零配置自动发现会把它们也拉进来——循环依赖夹具会让发现直接失败，
//   而这正是"自动发现是零配置的、逃生门是显式的"这一语义的证明。）

[DisableAutoDiscovery]
public static class CycleFixture
{
    [DependsOn(typeof(B))]
    public class A : IModule;

    [DependsOn(typeof(A))]
    public class B : IModule;
}

[DisableAutoDiscovery]
public static class OrphanFixture
{
    [DependsOn(typeof(Unregistered))]
    public class Orphan : IModule;

    public class Unregistered : IModule;
}

[DisableAutoDiscovery]
public static class RollbackFixture
{
    public class First : IModule
    {
        public void ConfigureServices(IServiceCollection services) =>
            Trace.Add("First:Configure");

        public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
        {
            Trace.Add("First:Init");
            return Task.CompletedTask;
        }

        public Task OnShutdownAsync(ModuleContext context, CancellationToken cancellationToken = default)
        {
            Trace.Add("First:Shutdown");
            return Task.CompletedTask;
        }
    }

    public class Second : IModule
    {
        public void ConfigureServices(IServiceCollection services) =>
            Trace.Add("Second:Configure");

        public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
        {
            Trace.Add("Second:Init");
            return Task.CompletedTask;
        }

        public Task OnShutdownAsync(ModuleContext context, CancellationToken cancellationToken = default)
        {
            Trace.Add("Second:Shutdown");
            return Task.CompletedTask;
        }
    }

    public class Failer : IModule
    {
        public void ConfigureServices(IServiceCollection services) =>
            Trace.Add("Failer:Configure");

        public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
        {
            Trace.Add("Failer:Init");
            throw new InvalidOperationException("boom");
        }
    }
}
