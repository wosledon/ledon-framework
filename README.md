# Ledon Framework

.NET 10 模块化框架。**解耦优先，靠自动发现和自动注入，把心智负担降到一行 `AddLedon()`。**

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLedon();   // 模块发现、依赖排序、服务注入、事件接线，全部自动完成
```

模块、服务、事件处理器都不需要手动注册——按约定即插即用。

## 写一个模块

```csharp
public class UserModule : IModule
{
    public void ConfigureServices(IServiceCollection services) { /* 可选：补充注册 */ }

    public async Task OnInitializedAsync(ModuleContext ctx, CancellationToken ct = default)
    {
        ctx.Logger.LogInformation("初始化，服务容器已可用: {Service}", ctx.Services.GetRequiredService<IGreeter>());
        await ctx.Events.PublishAsync(new UserCreated("alice"), ct);
    }
}
```

三个钩子全部可选，实现 `IModule` 即可被自动发现。依赖用特性声明，框架负责拓扑排序：

```csharp
[DependsOn(typeof(DataModule))]   // DataModule 先注册、先初始化、后关闭
public class UserModule : IModule;
```

## 模块间解耦：事件

发布方不需要知道谁在处理——任何 `IEventHandler<TEvent>` 都会被**自动发现并注入**：

```csharp
public record UserCreated(string Name);

public class WelcomeEmailSender : IEventHandler<UserCreated>
{
    public Task HandleAsync(UserCreated e, CancellationToken ct = default) { /* 发邮件 */ };
}
```

```csharp
await context.Events.PublishAsync(new UserCreated("alice"));   // 或注入 IEventBus 后发布
```

## 自动约定注册

扫描到的程序集里，每个具体类按以下规则自动进容器（`TryAdd`，模块自己的显式注册优先）：

| 规则       | 示例                               | 注册结果                                    |
| ---------- | ---------------------------------- | ------------------------------------------- |
| 同名接口   | `class UserService : IUserService` | `IUserService → UserService`（默认 Scoped） |
| 事件处理器 | `class X : IEventHandler<E>`       | `IEventHandler<E> → X`（Transient）         |
| 兜底自注册 | `class PlainService`               | `PlainService → PlainService`               |

泛型 arity 会被忽略（`IRepo<T>` ↔ `Repo<T>`）。默认生命周期可在 `AddLedon(o => o.DefaultServiceLifetime = ...)` 调整。

## 逃生门（默认全自动，需要时才用）

```csharp
builder.Services.AddLedon(o => o
    .DisableAutoDiscovery()      // 关闭"入口程序集 + 已加载程序集"的全局扫描
    .AddModule<UserModule>()     // 只注册显式列出的模块
    .AddAssembly(typeof(X).Assembly)   // 追加扫描一个程序集
    .DefaultServiceLifetime = ServiceLifetime.Transient);
```

```csharp
[DisableAutoDiscovery]   // 标记的类型（含嵌套）不参与自动扫描，只能显式 AddModule
public class MyFixture { }
```

## 生命周期

```
发现模块 → 依赖图（拓扑排序 + 环/缺失依赖检测，带路径报错）
       → 约定注入（服务 + 事件处理器）
       → 各模块 ConfigureServices（按拓扑序，容器 Build 之前）
       → host.StartAsync → OnInitializedAsync（按拓扑序）
       → 运行期：事件总线通信
       → host.StopAsync  → OnShutdownAsync（逆序）
```

初始化失败时已初始化的模块会**逆序回滚**，原始异常原样抛出，不留半启动状态。
不用通用主机时：`await provider.InitializeModulesAsync()` / `ShutdownModulesAsync()`。

## 项目结构

```
src/Ledon.Core/     事件基元（IEventBus / IEventHandler）——与模块系统无关的底层契约
src/Ledon.Module/   模块系统：契约、依赖图、运行时、宿主接入、约定扫描、事件总线
tests/              xunit 测试（顺序断言钉住生命周期轨迹）
samples/Ledon.Sample 零配置可运行示例：./run.ps1 sample
scripts/            任务编排：./run.ps1 [scan|build|test|sample|check]
```

## 常用命令

```powershell
./run.ps1 check    # scan → build → test
./run.ps1 sample   # 跑零配置示例
./run.ps1          # 列出所有任务
```

## 设计边界（v1 明确不做）

- 运行时动态加载 / 插件热插拔（当前为静态引用；发现环节已与加载方式解耦，后续可扩展）
- 模块级独立 DI 容器（所有模块共享根容器）
- 每模块独立配置（配置经 `ModuleContext.Configuration` 走宿主配置即可）
