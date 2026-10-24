# 模块系统

## 契约

```csharp
public interface IModule
{
    void ConfigureServices(IServiceCollection services) { }                 // 注册阶段（容器 Build 前）
    Task OnInitializedAsync(ModuleContext ctx, CancellationToken ct = default); // 初始化（按拓扑序）
    Task OnShutdownAsync(ModuleContext ctx, CancellationToken ct = default);    // 关闭（逆序）
}
```

三个钩子全部可选。

## 依赖声明

```csharp
[DependsOn(typeof(DataModule))]
public class UserModule : IModule;
```

框架构建依赖图并拓扑排序：被依赖者先注册、先初始化、后关闭。循环依赖启动即失败（报错带 `A -> B -> A` 路径）。

## 生命周期

```
发现程序集 → 约定注入 → ConfigureServices（拓扑序）
  → host.StartAsync → [基础设施前置任务] → OnInitializedAsync（拓扑序）→ 队列/调度器启动
  → 运行期：事件 / 队列 / 缓存 / 链路指标
  → host.StopAsync → OnShutdownAsync（逆序）
```

初始化失败时已初始化模块**逆序回滚**，原始异常原样抛出。

## 模块间解耦：事件

```csharp
public record UserCreated(string Name);

public class WelcomeEmailSender : IEventHandler<UserCreated>
{
    public Task HandleAsync(UserCreated e, CancellationToken ct = default) { /* ... */ }
}
```

```csharp
await context.Events.PublishAsync(new UserCreated("alice"));
```

处理器自动发现注入，发布方编译期不需要知道谁在处理。跨进程/要求可靠投递时改用[发件箱](data.md)。

## 自定义链路

```csharp
using var span = ctx.StartActivity("加载配置");   // 挂入框架链路树
```

## 并行初始化（可选）

模块多、初始化含 IO 等待时，同一依赖层（互不依赖）的模块可并发初始化：

```csharp
builder.Services.AddLedon(o => o.ParallelInitialization = true);
```

- 层与层之间仍保持拓扑序（被依赖者先初始化），只有同层并行；
- 同层模块的初始化顺序不再确定（日志/诊断顺序会变）——模块间有隐式依赖时用 `[DependsOn]` 显式声明；
- 失败回滚语义不变：已初始化的模块（含并行层中已完成的）按逆序关闭，原始异常原样抛出。
