# 自动发现

## 允许列表规则

一个程序集被扫描，当且仅当它**直接引用了框架程序集**（Ledon.* 功能包）——「引用框架 = 选择加入」。

候选来自三个通道（都过同一套允许列表，结果按程序集名排序，跨宿主确定）：

1. **依赖上下文（deps.json）**——记录全部声明的依赖，**与代码是否使用无关**（解决"引用了模块程序集但代码没直接用其类型"时编译器删引用导致的静默丢失）；
2. **已加载程序集**；
3. **已加载且引用框架的程序集的依赖上下文**——覆盖入口指向宿主的场景（VSTest）。

## 约定注册

| 规则       | 示例                               | 注册结果                               |
| ---------- | ---------------------------------- | -------------------------------------- |
| 同名接口   | `class UserService : IUserService` | `IUserService → UserService`（Scoped） |
| 事件处理器 | `class X : IEventHandler<E>`       | `IEventHandler<E> → X`（Transient）    |

**没有接口的类（DTO、控制器、端点）不会被自动注册**——把 MVC 动作参数类型注册进 DI 会让 `[ApiController]` 的隐式 `[FromBody]` 推断失效。

## 诊断

启动时 `Ledon.Module` 日志分类输出：

- **Information**：发现了哪些模块、扫描了哪些程序集
- **Debug**：被跳过的程序集清单、每条约定注册明细

## 逃生门

```csharp
builder.Services.AddLedon(o => o
    .DisableAutoDiscovery()      // 关闭全局扫描
    .AddModule<UserModule>()     // 只注册显式列出的
    .AddAssembly(typeof(X).Assembly));
```

```csharp
[DisableAutoDiscovery]   // 标记的类型（含嵌套）不参与自动扫描
public class MyFixture { }
```
