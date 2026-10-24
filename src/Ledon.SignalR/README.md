# Ledon.SignalR

Ledon Framework 的 SignalR 集成：Hub 自动发现 + 约定路径映射。

```csharp
builder.Services.AddLedonSignalR();
app.MapLedonSignalRHubs();   // ChatHub → /hubs/chat
```

```csharp
public class ChatHub : Hub
{
    public async Task Send(string user, string message) =>
        await Clients.All.SendAsync("Receive", user, message);
}
```

- Hub 类写在业务程序集里即被自动发现（沿用框架允许列表：引用框架 = 选择加入）；
- 路径约定：`/hubs/{类型名去 "Hub" 后缀，kebab-case}`——`ChatHub` → `/hubs/chat`，`NotificationHub` → `/hubs/notification`；
- Hub 里的依赖注入、`[Authorize]`、模块事件照常可用（Hub 按连接从 DI 解析）。

完整文档：<https://github.com/wosledon/ledon-framework>
