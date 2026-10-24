using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Sample.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Sample.Users;

/// <summary>用户业务模块（独立程序集）：注册用户服务，初始化时注册一个用户。</summary>
public class UserModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("UserModule 初始化（来自独立程序集）");

        var users = context.Services.GetRequiredService<IUserService>();
        users.Register("alice");

        return Task.CompletedTask;
    }
}

/// <summary>约定注册：IUserService ↔ UserService，无需手工 AddScoped。</summary>
public interface IUserService
{
    void Register(string userName);
}

public class UserService : IUserService
{
    private readonly IEventBus _events;

    public UserService(IEventBus events) => _events = events;

    public void Register(string userName)
    {
        // 发布事件：不知道谁在处理（通知模块在另一个程序集里消费）
        _events.PublishAsync(new UserRegistered(userName)).GetAwaiter().GetResult();
    }
}
