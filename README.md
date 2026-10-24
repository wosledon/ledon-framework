# Ledon Framework

.NET 10 模块化 **Web 开发框架**。解耦优先，靠自动发现和自动注入，把心智负担降到几行接入代码；能力拆成独立包，**用哪个引哪个**。

## 30 秒上手（Web）

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddLedonWeb(o => o.UseOpenApi = true);   // 模块系统 + 端点/控制器自动发现 + OpenAPI 按需
builder.Services.AddLedonData();                 // 数据访问按需引入：DbContext 自动注册 + 自动建库

var app = builder.Build();
app.UseLedonWeb();
app.Run();
```

没有 `AddModule`、没有 `AddControllers`、没有 `AddDbContext`、没有 `MapGet`——模块、服务、事件处理器、端点全部按约定自动接线。完整可运行示例：`./run.ps1 web`（多程序集布局）、`./run.ps1 worker`（非 Web 工作进程）。

## 能力矩阵（按需引入）

| 能力                                   | 包                            | 引入方式                                             | 说明                                             |
| -------------------------------------- | ----------------------------- | ---------------------------------------------------- | ------------------------------------------------ |
| 模块系统（发现/依赖/生命周期/事件）    | `Ledon.Module`                | 被 `Ledon.Web` 自动带入                              | 核心，默认在场                                   |
| Web 集成（端点/控制器/OpenAPI/规范化） | `Ledon.Web`                   | `builder.AddLedonWeb()`                              | 含 ProblemDetails、关联 ID、统一响应、版本化、CORS 开关 |
| 数据访问（EF Core + 发件箱）           | `Ledon.Data` / `Ledon.Queues` | `AddLedonData()` / `AddLedonOutbox()`                | 自动注册 + 前置自动建库 + 至少一次投递           |
| 缓存                                   | `Ledon.Caching`               | `AddLedonCaching()` / `AddLedonDistributedCaching()` | 内存（single-flight）/ 分布式（Redis）           |
| 后台任务                               | `Ledon.Queues`                | `AddLedonQueues()`                                   | Channel 有界 + 多 worker                         |
| 消息中间件传输                         | `Ledon.Queues.RabbitMQ`       | `AddLedonRabbitMqTransport()`                        | 发件箱 ↔ RabbitMQ，跨服务投递                   |
| 定时任务                               | `Ledon.Scheduling`            | `AddLedonScheduling()`                               | cron 驱动（5/6 段）                              |
| 认证授权                               | `Ledon.Security`              | `AddLedonSecurity()`                                 | JWT + API Key + 权限策略                         |
| 可观测性                               | `Ledon.Observability`         | `AddLedonObservability()`                            | OTel 链路/指标/日志 + 健康检查 + Prometheus |
| 结构化日志                             | `Ledon.Serilog`               | `builder.AddLedonSerilog()`                          | Serilog + TraceId/SpanId 富化              |
| 弹性策略                               | `Ledon.Resilience`            | `AddLedonResilience()`                               | 重试/超时/熔断，接管全部 HttpClient              |
| 强类型配置                             | `Ledon.Configuration`         | `AddLedonOptions<T>(config)`                         | 绑定 + 启动校验                                  |
| 多租户                                 | `Ledon.MultiTenancy`          | `AddLedonMultiTenancy()`                             | 租户解析 + 后台穿透                              |
| 测试基建                               | `Ledon.Testing`               | `LedonWebTestHost`                                   | 一行搭 TestServer + 生命周期断言                 |

不用的包不引用，零负担。所有包共享同一套自动发现（`LedonAssemblies.Scan`）与模块生命周期。

## 写一个模块

```csharp
public class UserModule : IModule
{
    public void ConfigureServices(IServiceCollection services) { /* 可选：补充注册 */ }

    public async Task OnInitializedAsync(ModuleContext ctx, CancellationToken ct = default)
    {
        ctx.Logger.LogInformation("初始化，服务容器已可用");
        await ctx.Events.PublishAsync(new UserCreated("alice"), ct);
    }
}
```

三个钩子全部可选。依赖用特性声明，框架负责拓扑排序（被依赖者先注册、先初始化、后关闭）：

```csharp
[DependsOn(typeof(DataModule))]
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

跨进程/要求可靠投递时用**发件箱**（至少一次）：

```csharp
public class OrderDbContext : OutboxDbContext;   // 继承即获得 OutboxMessages

// 业务事务里：事件与数据同一次 SaveChanges
outbox.Add(new OrderPlaced(orderId));
await db.SaveChangesAsync();   // 调度器随后投递并标记
```

## Web 开发

**最小 API 端点**——实现 `IEndpoint` 即被自动发现并映射：

```csharp
public class HelloEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
        => builder.MapGet("/hello", () => "Hello, Ledon!");
}
```

**控制器**——所在程序集自动挂为 MVC ApplicationPart，`[ApiController]` 直接可用：

```csharp
[ApiController]
[Route("api/todos")]
public class TodosController(TodoDbContext db) : ControllerBase
{
    [HttpGet]  public async Task<List<TodoItem>> GetAll(CancellationToken ct) => await db.Todos.ToListAsync(ct);
    [HttpPost] public async Task<TodoItem> Create(TodoItem item, CancellationToken ct) { /* ... */ }
}
```

**API 规范化**（`LedonWebOptions` 控制，均有默认值）：

| 开关                 | 默认 | 行为                                          |
| -------------------- | ---- | --------------------------------------------- |
| `UseProblemDetails`  | ✅    | 未处理异常 → RFC 7807（含关联 ID/TraceId）    |
| 模型验证             | ✅    | 失败统一 400 ValidationProblemDetails         |
| `UseCorrelationId`   | ✅    | `X-Correlation-Id` 贯穿请求/响应头/日志作用域 |
| `UseUnifiedResponse` | ❌    | 成功响应包 `{"code":0,"data":...}` 信封       |
| `UseApiVersioning`   | ❌    | URL 段 `api/v{version}/...`                   |
| `UseCors`            | ❌    | 跨域：按 `CorsOrigins` 白名单放行             |
| `UseScalarUi`        | ❌    | Scalar API 文档 UI（需同时开 UseOpenApi）     |

前后端分离（SPA）场景开跨域：

```csharp
builder.AddLedonWeb(o =>
{
    o.UseOpenApi = true;
    o.UseCors = true;
    o.CorsOrigins = ["https://app.example.com"];   // 空数组 = 允许任意来源（仅建议开发环境）
});
```

## 数据访问

实现 `ILedonDbContext` 即被自动注册，并在启动时（**先于任何模块初始化**）自动建库/迁移；发件箱继承 `OutboxDbContext`。自己管理 schema 时 `AddLedonData(o => o.AutoMigrate = false)`。

## 缓存

```csharp
builder.Services.AddLedonCaching();        // 进程内：同 key 并发未命中 single-flight
// 或多实例共享：
builder.Services.AddLedonDistributedCaching(o => o.RedisConnectionString = "localhost:6379");
```

## 后台队列 / 定时任务

```csharp
builder.Services.AddLedonQueues(o => o.WorkerCount = 4);
await queue.EnqueueAsync(async (scope, token) => { /* 独立作用域 */ });

builder.Services.AddLedonScheduling();
public class CleanupTask : IScheduledTask
{
    public string CronExpression => "0 3 * * *";   // 每天 3 点
    public Task ExecuteAsync(CancellationToken ct = default) { /* ... */ }
}
```

执行天然串行；耗时跨过触发点默认丢弃，`o.CatchUpMissedTriggers = true` 可补跑；多实例部署开 `o.LeaderElection = SchedulerLeaderElection.Redis` 仅领导实例执行。

## 认证授权

```csharp
builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = Configuration["Jwt:Key"]!);
// [Authorize]                        → JWT 或 API Key 任一通过
// [Authorize(Policy = "users.read")]  → 权限策略（策略名即权限名）
var token = provider.GetRequiredService<IJwtTokenGenerator>().Generate("u1", "alice", ["users.read"]);
```

访问令牌过期后用 `IRefreshTokenService` 轮换（哈希存储 + 重放保护），详见[认证授权文档](docs/security.md)。

## 可观测性

**框架自身即可观测**——模块生命周期、事件发布、队列处理全部产出 span，`AddLedonObservability()` 自动收听：

```csharp
builder.Services.AddLedonObservability(o =>
{
    o.ServiceName = "my-app";
    o.OtlpEndpoint = new Uri("http://localhost:4317");   // 或读 OTEL_EXPORTER_OTLP_ENDPOINT
    o.UseConsoleExporter = true;                          // 开发排障：span 直接打控制台
});

builder.Services.AddLedonHealthChecks();
app.MapLedonLiveness();    // /health/live  进程在跑即 200
app.MapLedonReadiness();   // /health/ready 聚合全部检查
```

模块内可发自定义 span：`using var span = ctx.StartActivity("加载配置");`

## 弹性策略 / 配置 / 多租户

```csharp
builder.Services.AddLedonResilience();                    // 重试+超时+熔断，接管全部 HttpClient
builder.Services.AddLedonOptions<SmtpOptions>(builder.Configuration);   // 绑定+启动校验
builder.Services.AddLedonMultiTenancy();                  // 租户解析（请求头/查询串/claim/子域名）+ 后台穿透
app.UseLedonMultiTenancy();
```

## 自动发现规则（允许列表）

一个程序集被扫描，当且仅当它**直接引用了框架程序集**（Ledon.* 功能包）——「引用框架 = 选择加入」。基础设施（EF Core、xUnit、TestPlatform……）不引用框架，天然排除；框架自身包显式排除。候选来自三个通道（都过同一套允许列表，结果按程序集名排序，跨宿主确定）：

1. **依赖上下文（deps.json）**——记录全部声明的依赖，**与代码是否使用无关**（解决"项目引用了模块程序集但代码没直接用其类型"时编译器删引用、运行库不加载导致的静默丢失）；
2. **已加载程序集**；
3. **已加载且引用框架的程序集的依赖上下文**——覆盖入口指向宿主的场景（VSTest）。

启动时 `Ledon.Module` 日志分类输出诊断：**Information** 报告模块与扫描集；**Debug** 给出跳过清单与约定注册明细。逃生门：`AddAssembly` / `AddModule<T>()` / `[DisableAutoDiscovery]`。

## 约定注册规则

扫描到的程序集里，每个具体类按以下规则自动进容器（`TryAdd`，手动注册优先）：

| 规则       | 示例                               | 注册结果                                    |
| ---------- | ---------------------------------- | ------------------------------------------- |
| 同名接口   | `class UserService : IUserService` | `IUserService → UserService`（默认 Scoped） |
| 事件处理器 | `class X : IEventHandler<E>`       | `IEventHandler<E> → X`（Transient）         |

**没有接口的类（DTO、控制器、端点）不会被自动注册**——把 MVC 动作参数类型注册进 DI 会让 `[ApiController]` 的隐式 `[FromBody]` 推断失效。此类类型请显式注册。

## 生命周期

```
发现程序集（允许列表三通道）
  → 诊断日志（Information：模块与扫描集；Debug：跳过清单与约定注册明细）
  → 约定注入（服务 + 事件处理器）
  → 各模块 ConfigureServices（拓扑序，容器 Build 之前）
  → host.StartAsync
      → 阶段一（基础设施）：ILedonPreInitialization——如建库/迁移
      → 阶段二（应用）：OnInitializedAsync（拓扑序）→ 队列 worker / 调度器启动
  → 运行期：事件 / 队列 / 缓存 / 链路指标
  → host.StopAsync  → OnShutdownAsync（逆序）
```

初始化失败时已初始化模块**逆序回滚**，原始异常原样抛出。不用通用主机：`await provider.InitializeModulesAsync()`。

## 多程序集布局（真实应用形态）

```
MyApp.sln
├─ src/MyApp.Api/               # 宿主（Web/Worker 均可）：AddLedonWeb() 一行接入
├─ src/Modules/MyApp.Contracts/ # 共享契约（事件 record）——只依赖 Ledon.Core
├─ src/Modules/MyApp.Users/     # 用户模块：引用 Contracts
└─ src/Modules/MyApp.Notifications/  # 通知模块：只引用 Contracts，不引用 Users
```

通知模块通过 `IEventHandler<UserRegistered>` 消费用户模块发布的事件，**两个业务程序集之间没有任何引用**——解耦不是约定俗成，而是编译期强制。示范：`./run.ps1 worker`、`./run.ps1 web`。

## 项目结构

```
src/Ledon.Core/         事件基元（IEventBus / IEventHandler）
src/Ledon.Module/       模块系统：契约、依赖图、运行时、约定扫描、程序集扫描
src/Ledon.Web/          Web 集成：端点、控制器、OpenAPI、API 规范化、关联 ID
src/Ledon.Data/         数据访问：ILedonDbContext、DbInitializer
src/Ledon.Caching/      缓存：内存（single-flight）/ 分布式（Redis）
src/Ledon.Queues/       后台队列 + 发件箱（Outbox）
src/Ledon.Scheduling/   定时任务（cron）
src/Ledon.Security/     认证授权（JWT / API Key / 权限策略）
src/Ledon.Observability OpenTelemetry + 健康检查
src/Ledon.Resilience/   弹性策略（重试/超时/熔断）
src/Ledon.Configuration/ 强类型配置约定
src/Ledon.MultiTenancy/ 多租户
src/Ledon.Testing/      集成测试基建
src/Ledon.Serilog/       Serilog 结构化日志集成
src/Ledon.Queues.RabbitMQ/ RabbitMQ 传输（发件箱 ↔ broker）
samples/Modules/        示例业务模块（Contracts / Users / Notifications / Api）
samples/                Ledon.Sample（控制台）/ Ledon.WebSample（Web）/ Ledon.WorkerSample（工作进程）
tests/                  xunit 测试（34+ 用例，顺序断言 + 变异验证）
scripts/                任务编排：./run.ps1 [scan|build|test|sample|web|worker|check]
docs/                   文档站（docfx）
```

## 常用命令

```powershell
./run.ps1 check    # scan → build → test
./run.ps1 docs     # 构建文档站（docfx，产物 _site/）
./run.ps1 web      # Web 示例（多程序集 + 全功能链路）
./run.ps1 worker   # 工作进程示例（跨程序集事件解耦）
./run.ps1 sample   # 控制台零配置示例
./run.ps1          # 列出所有任务
```

## 发布

CI（`.github/workflows/ci.yml`）在 push/PR 时 build+test；打 `v*` tag 时按版本打包并推 NuGet.org（需仓库 secret `NUGET_API_KEY`）。本地打包：`dotnet pack -c Release`。

## 设计边界（v1 明确不做）

- 运行时动态加载 / 插件热插拔（当前为静态引用；发现环节已与加载方式解耦，后续可扩展）
- 模块级独立 DI 容器（所有模块共享根容器）
- 每模块独立配置（配置经 `ModuleContext.Configuration` 走宿主配置即可）
- 多租户的隐式查询过滤（提供上下文与解析，隔离由查询层显式应用——约定显式，避免意外）
