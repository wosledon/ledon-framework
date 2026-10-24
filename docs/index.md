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

| 主题                                  | 内容                                       |
| ------------------------------------- | ------------------------------------------ |
| [快速开始](~/docs/getting-started.md) | 安装、第一个应用、运行示例                 |
| [模块系统](~/docs/modules.md)         | IModule、依赖排序、生命周期、事件          |
| [自动发现](~/docs/discovery.md)       | 允许列表规则、约定注册、逃生门             |
| [Web 开发](~/docs/web.md)             | 端点、控制器、API 规范化、CORS             |
| [数据与发件箱](~/docs/data.md)        | EF Core、Outbox 至少一次投递               |
| [缓存](~/docs/caching.md)             | 内存 single-flight / 分布式 Redis         |
| [后台队列](~/docs/queues.md)          | Channel 队列 + 发件箱投递                  || [定时任务](~/docs/scheduling.md)      | cron 驱动（5/6 段）、失败隔离              |
| [认证授权](~/docs/security.md)        | JWT + API Key + 权限策略                   |
| [可观测性](~/docs/observability.md)   | OTel 链路/指标、框架 span、健康检查        |
| [弹性策略](~/docs/resilience.md)      | 重试/超时/熔断，接管全部 HttpClient        |
| [强类型配置](~/docs/configuration.md) | 绑定 + 启动校验                            |
| [多租户](~/docs/multitenancy.md)      | 租户解析 + 后台穿透                        |
| [测试基建](~/docs/testing.md)          | TestServer 宿主 + 生命周期断言             |

## 能力包

模块系统（`Ledon.Module`）、Web（`Ledon.Web`）、数据（`Ledon.Data`）、缓存（`Ledon.Caching`）、队列与发件箱（`Ledon.Queues`）、RabbitMQ 传输（`Ledon.Queues.RabbitMQ`）、定时任务（`Ledon.Scheduling`）、认证授权（`Ledon.Security`）、可观测性（`Ledon.Observability`）、结构化日志（`Ledon.Serilog`）、弹性策略（`Ledon.Resilience`）、配置（`Ledon.Configuration`）、Nacos 配置中心（`Ledon.Configuration.Nacos`）、多租户（`Ledon.MultiTenancy`）、测试基建（`Ledon.Testing`）、云原生默认值（`Ledon.Aspire`）、实时推送（`Ledon.SignalR`）。

详见 [README](https://github.com/wosledon/ledon-framework) 的能力矩阵。
