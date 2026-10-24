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

没有 `AddModule`、没有 `AddControllers`、没有 `AddDbContext`、没有 `MapGet`——模块、服务、事件处理器、端点全部按约定自动接线。完整可运行示例：`./run.ps1 web`。

## 能力矩阵（按需引入）

| 能力 | 包 | 引入方式 | 说明 |
|---|---|---|---|
| 模块系统（发现/依赖/生命周期/事件） | `Ledon.Module` | 被 `Ledon.Web` 自动带入 | 核心，默认在场 |
| Web 集成（端点/控制器/OpenAPI） | `Ledon.Web` | `builder.AddLedonWeb()` | 控制器默认自动发现 |
| 数据访问（EF Core） | `Ledon.Data` | `builder.Services.AddLedonData()` | 自动注册 + 前置自动建库/迁移 |
| 缓存 | `Ledon.Caching` | `builder.Services.AddLedonCaching()` | `ICacheService`，single-flight 回源 |
| 后台队列 | `Ledon.Queues` | `builder.Services.AddLedonQueues()` | `IBackgroundQueue`，Channel 有界 + 多 worker |
| 可观测性 | `Ledon.Observability` | `builder.Services.AddLedonObservability()` | OpenTelemetry 链路/指标 + 健康检查 |
| OpenAPI 文档 | （随 `Ledon.Web`） | `o.UseOpenApi = true` | `/openapi/v1.json` |

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

```csharp
await context.Events.PublishAsync(new UserCreated("alice"));   // 或注入 IEventBus 后发布
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

**OpenAPI**——`o.UseOpenApi = true` 即提供 `/openapi/v1.json`（.NET 10 内置）。

## 数据访问

实现 `ILedonDbContext` 即被自动注册，并在启动时（**先于任何模块初始化**）自动建库/迁移：

```csharp
public class TodoDbContext : DbContext, ILedonDbContext
{
    public DbSet<TodoItem> Todos => Set<TodoItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite("Data Source=app.db");
}
```

有挂起迁移走 `Migrate`，否则 `EnsureCreated`——两种工作流都无需手动处理。自己管理 schema 时 `AddLedonData(o => o.AutoMigrate = false)`。

## 缓存

```csharp
builder.Services.AddLedonCaching(o => o.DefaultExpiration = TimeSpan.FromMinutes(10));

public class ProductService(ICacheService cache)
{
    public Task<Product> GetAsync(int id, CancellationToken ct) =>
        cache.GetOrCreateAsync($"product:{id}", async token =>
        {
            // 未命中才回源；同一 key 的并发未命中共享一次回源（single-flight）
            return await LoadFromDbAsync(id, token);
        }, cancellationToken: ct);
}
```

v1 为进程内内存实现；换分布式缓存（Redis 等）只需替换 `ICacheService` 实现，调用方不变。

## 后台队列

```csharp
builder.Services.AddLedonQueues(o => o.WorkerCount = 4);   // 4 个并发 worker

public class OrderService(IBackgroundQueue queue)
{
    public ValueTask SendEmailAsync(int orderId) =>
        queue.EnqueueAsync(async (scope, ct) =>
        {
            // 每个任务都在独立 DI 作用域中执行，可安全解析 Scoped 服务
            var mailer = scope.GetRequiredService<IMailer>();
            await mailer.SendAsync(orderId, ct);
        });
}
```

有界队列提供背压；单个任务失败只记录日志并继续，不会拖垮 worker 或影响后续任务。

## 可观测性

```csharp
// 链路 + 指标：ASP.NET Core / HttpClient / 运行时自动插桩
builder.Services.AddLedonObservability(o =>
{
    o.ServiceName = "my-app";
    o.OtlpEndpoint = new Uri("http://localhost:4317");   // 不配则读 OTEL_EXPORTER_OTLP_ENDPOINT
});

// 健康检查：IHealthCheck 实现被自动发现
builder.Services.AddLedonHealthChecks();
app.MapLedonHealthChecks();   // GET /health
```

## 约定注册规则

扫描到的程序集里，每个具体类按以下规则自动进容器（`TryAdd`，手动注册优先）：

| 规则 | 示例 | 注册结果 |
|---|---|---|
| 同名接口 | `class UserService : IUserService` | `IUserService → UserService`（默认 Scoped） |
| 事件处理器 | `class X : IEventHandler<E>` | `IEventHandler<E> → X`（Transient） |

**没有接口的类（DTO、控制器、端点）不会被自动注册**——这不仅是减少噪音：把 MVC 动作参数类型注册进容器会让 `[ApiController]` 的隐式 `[FromBody]` 推断失效（退化为查询绑定）。此类类型请显式注册。泛型 arity 会被忽略（`IRepo<T>` ↔ `Repo<T>`）。默认生命周期可用 `o.DefaultServiceLifetime` 调整。

## 逃生门（默认全自动，需要时才用）

```csharp
// Web 层
builder.AddLedonWeb(
    o => o.UseOpenApi = true,                                  // Web 开关
    m => m.DisableAutoDiscovery().AddModule<UserModule>());    // 转发给模块系统

// 各功能包
builder.Services.AddLedonData(o => o.AutoMigrate = false);
builder.Services.AddLedonCaching(o => o.KeyPrefix = "app");
builder.Services.AddLedonQueues(o => o.WorkerCount = 4);
```

```csharp
[DisableAutoDiscovery]   // 标记的类型（含嵌套）不参与自动扫描，只能显式 AddModule
public class MyFixture { }
```

## 生命周期

```
发现程序集（入口引用图 BFS ∪ 已加载程序集，自动排除框架与基础设施）
  → 约定注入（服务 + 事件处理器）
  → 各模块 ConfigureServices（拓扑序，容器 Build 之前）
  → [Ledon.Data] 前置任务：建库/迁移（ILedonPreInitialization，先于所有模块）
  → host.StartAsync → OnInitializedAsync（拓扑序）→ [Ledon.Queues] worker 启动
  → 运行期：事件总线通信 / 后台队列处理 / 缓存 / 链路指标
  → host.StopAsync  → OnShutdownAsync（逆序）
```

初始化失败时已初始化的模块会**逆序回滚**，原始异常原样抛出。不用通用主机时：`await provider.InitializeModulesAsync()` / `ShutdownModulesAsync()`。

## 项目结构

```
src/Ledon.Core/        事件基元（IEventBus / IEventHandler）——与模块系统无关的底层契约
src/Ledon.Module/      模块系统：契约、依赖图、运行时、宿主接入、约定扫描、程序集扫描
src/Ledon.Web/         Web 集成：IEndpoint 自动发现、控制器 ApplicationPart、OpenAPI 开关
src/Ledon.Data/        数据访问：ILedonDbContext 自动注册、DbInitializer 前置建库/迁移
src/Ledon.Caching/     缓存：ICacheService + 内存实现（single-flight）
src/Ledon.Queues/      后台队列：IBackgroundQueue + Channel 有界实现 + 多 worker 宿主服务
src/Ledon.Observability OpenTelemetry 链路/指标 + 健康检查自动发现
tests/                 xunit 测试（顺序断言钉住生命周期轨迹 + Web 全链路 + 功能包）
samples/               Ledon.Sample（控制台零配置）/ Ledon.WebSample（Web 全特性）
scripts/               任务编排：./run.ps1 [scan|build|test|sample|web|check]
```

## 常用命令

```powershell
./run.ps1 check    # scan → build → test
./run.ps1 web      # 跑 Web 示例（全功能链路）
./run.ps1 sample   # 跑控制台零配置示例
./run.ps1          # 列出所有任务
```

## 路线图（v1 之后）

- 分布式缓存实现（Redis：`IDistributedCache` 适配 `ICacheService`）
- 外部 Broker 队列实现（RabbitMQ / Kafka，替换 `IBackgroundQueue` 实现即可）
- 认证授权包（JWT / API Key，`Ledon.Security`）
- 插件热插拔（`AssemblyLoadContext`，发现环节已与加载方式解耦）

## 设计边界（v1 明确不做）

- 运行时动态加载 / 插件热插拔（当前为静态引用；发现环节已与加载方式解耦，后续可扩展）
- 模块级独立 DI 容器（所有模块共享根容器）
- 每模块独立配置（配置经 `ModuleContext.Configuration` 走宿主配置即可）
