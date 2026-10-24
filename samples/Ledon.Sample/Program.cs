using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 整个示例只做一件事：一行 AddLedon()，其余全靠自动发现 + 自动注入。
// 没有任何 AddModule / AddEventHandler / AddScoped 调用——模块、服务、事件处理器全部自动接线。

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLedon();

using var host = builder.Build();
await host.StartAsync();          // 模块自动初始化（按依赖拓扑序）

var greeter = host.Services.GetRequiredService<IGreeter>();   // 服务是约定自动注册的
Console.WriteLine(greeter.Greet("Ledon"));

await host.StopAsync();           // 模块自动关闭（逆序）

// ---------------- 模块 ----------------

/// <summary>被 GreetingModule 依赖：保证 GreetingModule 在它之后初始化。</summary>
public class ClockModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("[ClockModule] 就绪");
        return Task.CompletedTask;
    }
}

/// <summary>问候模块：依赖 ClockModule；初始化时发布事件（不知道谁在处理）。</summary>
[DependsOn(typeof(ClockModule))]
public class GreetingModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("[GreetingModule] 就绪，发布 GreetingStarted 事件");
        return context.Events.PublishAsync(new GreetingStarted("Ledon"), cancellationToken);
    }
}

/// <summary>通知模块：通过事件处理器消费 GreetingStarted——与发布方零直接引用。</summary>
public class NotificationModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("[NotificationModule] 就绪，等待事件");
        return Task.CompletedTask;
    }
}

// ---------------- 服务（约定注册：IGreeter ↔ Greeter、IClockService ↔ ClockService） ----------------

public interface IGreeter
{
    string Greet(string name);
}

public class Greeter(IClockService clock) : IGreeter
{
    public string Greet(string name) => $"你好，{name}！（当前时间 {clock.Now}）";
}

public interface IClockService
{
    string Now { get; }
}

public class ClockService : IClockService
{
    public string Now => DateTime.Now.ToString("HH:mm:ss");
}

// ---------------- 事件（处理器被自动发现并注入，无需手动注册） ----------------

public record GreetingStarted(string Name);

public class GreetingStartedHandler : IEventHandler<GreetingStarted>
{
    private readonly ILogger<GreetingStartedHandler> _logger;

    public GreetingStartedHandler(ILogger<GreetingStartedHandler> logger) => _logger = logger;

    public Task HandleAsync(GreetingStarted @event, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[Handler] 收到问候事件: {Name}（发布方不知道我在处理）", @event.Name);
        return Task.CompletedTask;
    }
}
