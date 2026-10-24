---
_layout: landing
---

# Ledon Framework

.NET 10 模块化 **Web 开发框架**。解耦优先，靠自动发现和自动注入，把心智负担降到几行接入代码；能力拆成独立包，**用哪个引哪个**。

## 30 秒上手

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddLedonWeb(o => o.UseOpenApi = true);   // 模块 + 端点/控制器 + OpenAPI
builder.Services.AddLedonData();                 // 数据访问（按需）

var app = builder.Build();
app.UseLedonWeb();
app.Run();
```

没有 `AddModule`、没有 `AddControllers`、没有 `MapGet`——模块、服务、事件处理器、端点全部按约定自动接线。

## 文档导航

| 主题                                  | 内容                              |
| ------------------------------------- | --------------------------------- |
| [快速开始](~/docs/getting-started.md) | 安装、第一个应用、运行示例        |
| [模块系统](~/docs/modules.md)         | IModule、依赖排序、生命周期、事件 |
| [自动发现](~/docs/discovery.md)       | 允许列表规则、约定注册、逃生门    |
| [Web 开发](~/docs/web.md)             | 端点、控制器、API 规范化          |
| [数据与发件箱](~/docs/data.md)        | EF Core、Outbox 至少一次投递      |
## 能力包

模块系统（`Ledon.Module`）、Web（`Ledon.Web`）、数据（`Ledon.Data`）、缓存（`Ledon.Caching`）、队列与发件箱（`Ledon.Queues`）、定时任务（`Ledon.Scheduling`）、认证授权（`Ledon.Security`）、可观测性（`Ledon.Observability`）、弹性策略（`Ledon.Resilience`）、配置（`Ledon.Configuration`）、多租户（`Ledon.MultiTenancy`）、测试基建（`Ledon.Testing`）。

详见 [README](https://github.com/wosledon/ledon-framework) 的能力矩阵。
