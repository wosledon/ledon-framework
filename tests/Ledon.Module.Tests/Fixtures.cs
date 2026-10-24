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

// ---------- 正常模块（顶层类型，参与程序集扫描） ----------

/// <summary>依赖 DataModule 的业务模块。</summary>
[DependsOn(typeof(DataModule))]
public class UserModule : IModule
{
    public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
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
    public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
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

/// <summary>验证注册阶段 <see cref="ModuleContext.Services"/> 不可访问。</summary>
public class GuardedModule : IModule
{
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        try
        {
            _ = context.Services;
            Trace.Add("Guarded:no-throw");
        }
        catch (LedonModuleException)
        {
            Trace.Add("Guarded:throws");
        }
    }
}

// ---------- 事件 ----------

public record GreetEvent(string Name);

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

/// <summary>初始化时通过事件总线向其它模块发布事件。</summary>
public class PublisherModule : IModule
{
    public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
        services.AddTransient<IEventHandler<GreetEvent>, GreetHandler>();

    public async Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        Trace.Add("Publisher:Init");
        await context.Events.PublishAsync(new GreetEvent("ledon"), cancellationToken);
    }
}

// ---------- 约定扫描夹具 ----------

public interface ITestUserService
{
    string Name { get; }
}

public class TestUserService : ITestUserService
{
    public string Name => "svc";
}

public class PlainService;

// ---------- 异常依赖夹具（嵌套类型：不参与程序集扫描，只能显式注册） ----------

public static class CycleFixture
{
    [DependsOn(typeof(B))]
    public class A : IModule;

    [DependsOn(typeof(A))]
    public class B : IModule;
}

public static class OrphanFixture
{
    [DependsOn(typeof(Unregistered))]
    public class Orphan : IModule;

    public class Unregistered : IModule;
}

/// <summary>初始化失败回滚夹具（嵌套类型：仅显式注册）。Failer 在初始化时抛出，验证已初始化模块被逆序关闭。</summary>
public static class RollbackFixture
{
    public class First : IModule
    {
        public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
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
        public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
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
        public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
            Trace.Add("Failer:Configure");

        public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
        {
            Trace.Add("Failer:Init");
            throw new InvalidOperationException("boom");
        }
    }
}
