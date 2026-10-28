# Ledon.Core

Ledon Framework 的事件基元：`IEventBus` / `IEventHandler<T>`。

进程内事件总线，发布方不需要知道谁在处理——任何 `IEventHandler<TEvent>` 都会被框架自动发现并注入。

```csharp
public record UserCreated(string Name);

public class WelcomeEmailSender : IEventHandler<UserCreated>
{
    public Task HandleAsync(UserCreated e, CancellationToken ct = default) { /* 发邮件 */ }
}

await ctx.Events.PublishAsync(new UserCreated("alice"), ct);
```

跨进程/要求可靠投递时用 `Ledon.Queues` 的发件箱（至少一次语义）。

- 处理器按容器注册顺序执行；默认"失败即停"并向上传播，可切换为隔离模式（单个失败记录后继续）；
- 编译期不知道事件类型时用 `PublishDynamicAsync`（如领域事件拦截器），按运行时类型分发；
- 只依赖 `Microsoft.Extensions.*` 抽象，不绑定 Web/数据层，可被任意宿主复用。

完整文档：<https://github.com/wosledon/ledon-framework>
