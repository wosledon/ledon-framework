using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Abstractions;
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
