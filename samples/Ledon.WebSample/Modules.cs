using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Ledon.WebSample;

/// <summary>问候模块：依赖 ClockModule，初始化时发布事件（不知道谁在处理）。</summary>
[DependsOn(typeof(ClockModule))]
public class GreetingModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("GreetingModule 初始化（依赖 ClockModule，故晚于它）");
        return context.Events.PublishAsync(new AppStarted(), cancellationToken);
    }
}

/// <summary>时钟模块：被 GreetingModule 依赖，先初始化。</summary>
public class ClockModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("ClockModule 初始化");
        return Task.CompletedTask;
    }
}

public record AppStarted;

/// <summary>事件处理器：自动发现并注入，无需任何手动注册。</summary>
public class AppStartedHandler : IEventHandler<AppStarted>
{
    private readonly ILogger<AppStartedHandler> _logger;

    public AppStartedHandler(ILogger<AppStartedHandler> logger) => _logger = logger;

    public Task HandleAsync(AppStarted @event, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("收到 AppStarted 事件（发布方不知道谁在处理）");
        return Task.CompletedTask;
    }
}

/// <summary>定时任务：实现 IScheduledTask 即按 cron 自动执行（每秒一次，示例窗口内能观察到）。</summary>
public class DemoTickTask : IScheduledTask
{
    private int _ticks;

    public string CronExpression => "*/1 * * * * *";

    public int Ticks => Volatile.Read(ref _ticks);

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _ticks);
        return Task.CompletedTask;
    }
}

/// <summary>SignalR Hub：自动发现并映射到 /hubs/demo。</summary>
public class DemoHub : Hub
{
    public Task Echo(string message) => Clients.Caller.SendAsync("Echo", message);
}

/// <summary>gRPC 服务实现：继承 protoc 生成的 Demo.DemoBase，经 AddLedonGrpc 自动映射。</summary>
public class DemoGrpcService : Grpc.Demo.DemoBase
{
    public override Task<Grpc.PingReply> Ping(Grpc.PingRequest request, global::Grpc.Core.ServerCallContext context) =>
        Task.FromResult(new Grpc.PingReply { Reply = $"pong: {request.Message}" });
}
