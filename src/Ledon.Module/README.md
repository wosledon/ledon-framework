# Ledon.Module

Ledon Framework 的模块系统（核心，默认在场）：模块自动发现、依赖拓扑排序、生命周期管理、约定注入、程序集允许列表扫描。

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddLedonWeb();   // 模块系统零配置接入
```

```csharp
[DependsOn(typeof(DataModule))]
public class UserModule : IModule
{
    public void ConfigureServices(IServiceCollection services) { /* 可选 */ }

    public async Task OnInitializedAsync(ModuleContext ctx, CancellationToken ct = default)
    {
        await ctx.Events.PublishAsync(new UserCreated("alice"), ct);
    }
}
```

- 引用框架程序集的业务程序集自动纳入扫描（「引用框架 = 选择加入」）；
- 约定注入：同名接口 / `IEventHandler<T>` 自动注册（手动注册优先）；
- 生命周期：ConfigureServices（拓扑序）→ 前置初始化 → OnInitializedAsync（拓扑序）→ OnShutdownAsync（逆序），初始化失败自动逆序回滚。

完整文档：<https://github.com/wosledon/ledon-framework>
