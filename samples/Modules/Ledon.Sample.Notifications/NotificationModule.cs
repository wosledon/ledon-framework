using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Sample.Contracts;
using Microsoft.Extensions.Logging;

namespace Ledon.Sample.Notifications;

/// <summary>通知业务模块（独立程序集）。</summary>
public class NotificationModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("NotificationModule 初始化（来自独立程序集）");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 跨程序集事件处理器：被自动发现并注入。
/// 注意本程序集没有引用 Ledon.Sample.Users——消费方对发布方零依赖。
/// </summary>
public class WelcomeEmailSender : IEventHandler<UserRegistered>
{
    private readonly ILogger<WelcomeEmailSender> _logger;

    public WelcomeEmailSender(ILogger<WelcomeEmailSender> logger) => _logger = logger;

    public Task HandleAsync(UserRegistered @event, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("发送欢迎邮件给 {UserName}（处理器在另一个程序集，发布方不知道我存在）", @event.UserName);
        return Task.CompletedTask;
    }
}
