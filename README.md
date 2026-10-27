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

没有 `AddModule`、没有 `AddControllers`、没有 `AddDbContext`、没有 `MapGet`——模块、服务、事件处理器、端点全部按约定自动接线。完整可运行示例：`./run.ps1 web`（多程序集布局）、`./run.ps1 worker`（非 Web 工作进程）、`./run.ps1 protocol`（协议栈）、`./run.ps1 stream`（RTMP 媒体面）。

## 能力矩阵（按需引入）

| 能力                                   | 包                            | 引入方式                                             | 说明                                             |
| -------------------------------------- | ----------------------------- | ---------------------------------------------------- | ------------------------------------------------ |
| 模块系统（发现/依赖/生命周期/事件）    | `Ledon.Module`                | 被 `Ledon.Web` 自动带入                              | 核心，默认在场；处理器错误可隔离；可选并行初始化   |
| Web 集成（端点/控制器/OpenAPI/规范化） | `Ledon.Web`                   | `builder.AddLedonWeb()`                              | 含 ProblemDetails、关联 ID、统一响应、版本化、CORS、限流、压缩、请求超时、幂等 |
| 数据访问（EF Core + 发件箱）           | `Ledon.Data` / `Ledon.Queues` | `AddLedonData()` / `AddLedonOutbox()`                | 自动注册 + 前置自动建库 + 至少一次投递 + 审计/软删除 + 领域事件 + 规约/分页/仓储 |
| 可靠投递（收件箱去重/死信管理）        | `Ledon.Queues`                | `AddLedonInbox()`                                    | 至少一次投递 + 恰好一次处理；死信查询/重放         |
| 缓存                                   | `Ledon.Caching`               | `AddLedonCaching()` / `AddLedonDistributedCaching()` / `AddLedonHybridCaching()` | 内存（single-flight）/ 分布式（Redis）/ 混合两级；多租户键自动隔离 |
| 后台任务                               | `Ledon.Queues`                | `AddLedonQueues()` / `AddLedonDurableQueue()`         | 进程内队列（有界+多 worker+停机 drain）/ 持久化队列（重启不丢） || 消息中间件传输                         | `Ledon.Queues.RabbitMQ` / `Ledon.Queues.Kafka` | `AddLedonRabbitMqTransport()` / `AddLedonKafkaTransport()` | 发件箱 ↔ broker，跨服务投递；Kafka 消费侧带死信主题 |
| 定时任务                               | `Ledon.Scheduling`            | `AddLedonScheduling()`                               | cron 驱动（5/6 段）+ 执行历史 + 手动触发          |
| 认证授权                               | `Ledon.Security`              | `AddLedonSecurity()`                                 | JWT + API Key + 权限策略                          |
| OIDC 服务器                            | `Ledon.OpenIddict`            | `builder.AddLedonOpenIddict()`                       | 标准 /connect/token + 内存/EF 共享存储（多实例） |
| 可观测性                               | `Ledon.Observability`         | `AddLedonObservability()`                            | OTel 链路/指标/日志 + 健康检查（含启动探针）+ Prometheus |
| 结构化日志                             | `Ledon.Serilog`               | `builder.AddLedonSerilog()`                          | Serilog + TraceId/SpanId 富化              |
| 实时推送                               | `Ledon.SignalR`               | `AddLedonSignalR()`                                  | Hub 自动发现 + 约定路径映射 + Redis backplane    |
| 弹性策略                               | `Ledon.Resilience`            | `AddLedonResilience()`                               | 重试/超时/熔断，接管全部 HttpClient              |
| 强类型配置                             | `Ledon.Configuration`         | `AddLedonOptions<T>(config)`                         | 绑定 + 启动校验                                  |
| Nacos 配置中心                         | `Ledon.Configuration.Nacos`   | `Configuration.AddLedonNacos()`                      | 真实长轮询 + 热更新 + 鉴权/故障转移               |
| Apollo 配置中心                        | `Ledon.Configuration.Apollo`  | `Configuration.AddLedonApollo()`                     | notifications/v2 长轮询 + 热更新                  |
| FluentValidation                      | `Ledon.FluentValidation`      | `AddLedonFluentValidation()`                         | 验证器自动发现 + 统一 400                        |
| 多租户                                 | `Ledon.MultiTenancy`          | `AddLedonMultiTenancy()`                             | 租户解析 + 后台穿透 + 查询过滤助手                |
| 测试基建                               | `Ledon.Testing`               | `LedonWebTestHost`                                   | 一行搭 TestServer + 生命周期断言                 |
| 云原生默认值                           | `Ledon.Aspire`                | `AddLedonServiceDefaults()`                          | OTel+健康检查+弹性+服务发现一次装齐              |
| gRPC 服务间通信                       | `Ledon.Grpc`                  | `AddLedonGrpc()` / `AddLedonGrpcClient<T>()`         | 服务自动发现 + 约定映射 + 强类型客户端 + 健康服务/日志拦截器 |
| 工作流引擎                            | `Ledon.Workflow`              | `AddLedonWorkflow()`                                 | 多步骤编排 + 重试 + Saga 补偿 + 重启恢复            |
| 统一事务入口                         | `Ledon.Transactions`          | `AddLedonTransactions()`                             | IUnitOfWork（本地事务）+ Saga（分布式补偿），同一种写法 |
| 分布式锁                             | `Ledon.Locking`               | `AddLedonLocking()`                                  | 进程内 / Redis 互斥（防重、资源独占），到期自动释放 |
| 功能开关 / 灰度                      | `Ledon.FeatureManagement`     | `AddLedonFeatureManagement()`                        | 开关 + 租户/用户白名单 + 百分比灰度 + 配置热更新 |
| 文件 / 对象存储                      | `Ledon.BlobStorage`           | `AddLedonBlobStorage()`                              | IFileStorage（本地/内存/S3 兼容 MinIO）+ 租户目录隔离 + 类型/大小校验 |
| 通知发送                             | `Ledon.Notifications`         | `AddLedonNotifications()`                            | 邮件/短信/推送统一入口 + 模板 + 重试 + 发送记录 |
| 多语言 / 请求文化                    | `Ledon.Localization`          | `AddLedonLocalization()`                             | 查询串/Cookie/Accept-Language 解析 + JSON 文案 |
| 操作审计                             | `Ledon.AuditLog`              | `AddLedonAudit()` / `AddLedonAudit<TContext>()`      | 谁何时改了什么（属性级 before/after）+ 保留策略 |
| 应用层管线（CQRS）                   | `Ledon.Application`           | `AddLedonApplication()`                              | ICommand/IQuery + 验证/日志/事务/缓存 behaviors |
| 统一运维端点                         | `Ledon.Admin`                 | `AddLedonAdmin()`                                    | 发件箱/死信/队列指标/工作流/定时任务一页总览 |
| 飞书（通知 + 事件回调）               | `Ledon.Notifications.Feishu`  | `AddLedonFeishu()`                                  | 机器人/应用发消息 + URL 校验/解密/事件分发 |
| 协议解析（帧/包/结构体映射）           | `Ledon.Protocols`             | `PacketSerializer<T>`                               | 编译式结构体映射（零反射零装箱）+ 帧编解码 |
| Socket 客户端                          | `Ledon.Sockets.Client`      | `AddLedonSocketClient()`                          | TCP/UDP 连接 + 断线指数退避重连 + 类型化收发 |
| Socket 服务端                          | `Ledon.Sockets.Server`      | `AddLedonSocketServer()`                          | TCP/UDP 监听 + 发送串行/超时 + 空闲超时 + 连接上限 |
| SIP 协议解析                           | `Ledon.Sip`                   | `SipParser` / `SdpParser`                         | RFC 3261 消息（obs-fold/多值保序）+ SDP + 类型化头 |
| SIP 服务端                             | `Ledon.Sip.Server`            | `AddLedonSipServer()`                             | 基于 Socket 库：UDP/TCP + 事务层（响应重传/ACK/定时器） |
| SIP 客户端                             | `Ledon.Sip.Client`            | `AddLedonSipClient()`                             | 基于 Socket 库：事务 + 对话 + ACK/CANCEL + 重传超时 |
| 流媒体（独立媒体面）                    | `Ledon.Streaming`             | `AddLedonStreaming()`                              | 进程内 RTMP 服务端（保活/鉴权/过期回收）+ HTTP-FLV/HLS + 在线状态/事件/踢人 |
| 出站 Webhook                           | `Ledon.Webhooks`              | `AddLedonWebhooks()`                               | 订阅 + HMAC 签名 + 重试/死信/重放 + 管理端点 |
| 短信渠道                               | `Ledon.Notifications.Sms`     | `AddLedonSmsNotifications()`                       | Webhook / Twilio + 统一通知路由 |
| 推送渠道                               | `Ledon.Notifications.Push`    | `AddLedonPushNotifications()`                      | Webhook / FCM HTTP v1 + 统一通知路由 |
| 分布式 ID                              | `Ledon.Ids`                   | `AddLedonIds()`                                    | ULID / GUID v7 / Snowflake，多实例 WorkerId 防冲突 |
| 数据播种                               | `Ledon.Data.Seeding`          | `AddLedonDataSeeding()`                            | IDataSeeder 自动发现 + Order 顺序 + 失败策略 |
| 运行时设置                             | `Ledon.Settings`             | `AddLedonSettings()`                               | 强类型读写 + Data Protection 加密 + JSON 文件持久化 |
| 搜索                                   | `Ledon.Search`                | `AddLedonSearch()`                                 | 内存 / Meilisearch + 租户过滤 |
| 外部登录                               | `Ledon.Security.ExternalLogins` | `AddLedonExternalLogins()`                       | OIDC / GitHub + Data Protection state + 框架双令牌 |
| 租户管理                               | `Ledon.MultiTenancy.Management` | `AddLedonTenantManagement()`                      | 租户 CRUD + 开通/回收钩子 + 管理端点 |
| 列级加密                               | `Ledon.Data.Encryption`       | `AddLedonDataEncryption()`                         | `[Encrypted]` + EF ValueConverter + Data Protection |
| 集成测试容器                           | `Ledon.Testing.Containers`    | `LedonContainers` / `*Fixture`                     | Redis / PostgreSQL / RabbitMQ / Kafka / MinIO |
| 动态 API Key                           | `Ledon.Security.ApiKeys`      | `AddLedonApiKeys()`                                | SHA-256 哈希 + scopes/租户/过期/吊销 + `LedonApiKey` 方案 |
| 共享令牌存储                           | `Ledon.Security.TokenStores`  | `AddLedonDistributedTokenStores()`                 | IDistributedCache 刷新令牌存储 + 单条/按用户吊销 |
| 固定窗口配额/限流                      | `Ledon.Quotas`                | `AddLedonQuotas()`                                 | 多实例缓存存储 + 429/Retry-After + 自定义配额键 |
| 租户数据库路由                         | `Ledon.Data.MultiTenancy`     | `AddLedonTenantData<TContext>()`                   | 租户连接串解析 + 按租户 DbContext + 迁移编排 |
| TOTP 双因素                            | `Ledon.Security.Totp`         | `AddLedonTotp()`                                   | RFC 6238 + Base32 + otpauth URI + 一次性恢复码 |
| 账号生命周期                           | `Ledon.Security.Accounts`     | `AddLedonAccounts()`                               | PBKDF2 + 邮箱确认 + 失败锁定 + 密码重置端点 |
| 出站 API 客户端                        | `Ledon.ApiClients`            | `AddLedonApiClient()` / `AddLedonApiClient<T>()`   | client_credentials 令牌缓存 + 关联 ID/traceparent |
| Vault 密钥                             | `Ledon.Secrets`               | `AddLedonSecrets()` / `Configuration.AddLedonVault()` | KV v1/v2 + Token/TokenFile + 配置源 + TTL 缓存 |
| CSV/Excel 数据交换                     | `Ledon.DataExchange`          | `AddLedonDataExchange()`                           | RFC 4180 CSV + OpenXML 流式 Excel + 错误行报告 |
| 图像处理                               | `Ledon.Imaging`               | `AddLedonImaging()`                                | PNG/JPEG/WebP + Fit/Fill + 缩略图 + 文件存储集成 |
| 搜索索引同步                           | `Ledon.Search.Sync`           | `AddLedonSearchSync()`                             | 队列 + 后台 Worker + 指数退避 + 超限丢弃 |
| 模块迁移编排                           | `Ledon.Data.Migrations`       | `AddLedonMigration<TContext>()`                    | 模块状态查询 + 顺序执行 + 失败隔离 |
| 幂等/防重复提交                        | `Ledon.Idempotency`           | `AddLedonIdempotency()`                            | 请求指纹 + 响应重放 + 409 冲突 + 并发/多实例去重 |
| RBAC 授权                              | `Ledon.Security.Authorization` | `AddLedonAuthorization()`                         | 角色/权限/用户分配 + 通配符 + 权限缓存 |
| API 版本治理                           | `Ledon.ApiVersioning`         | `AddLedonApiVersioning()`                          | 多来源版本读取 + 默认版本 + Deprecation/Sunset |
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

处理器失败默认"失败即停"（异常中断后续并传播）；旁路订阅方（邮件/审计）不想互相拖累时切隔离模式——单个失败被记录后继续其余处理器，全部完成后首个失败原样抛出（不静默）：

```csharp
builder.Services.AddLedon(o => o.Events.HandlerErrorHandling = EventHandlerErrorHandling.Isolate);
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
| `UseIdempotency`     | ❌    | `Idempotency-Key` 幂等（需 IDistributedCache）|
| `UseRateLimiting`    | ❌    | 固定窗口限流（分区键 `X-Api-Key` 或客户端 IP，超限 429）|
| `UseResponseCompression` | ❌ | Brotli/Gzip 响应压缩                         |
| `UseRequestTimeouts` | ❌    | 请求超时（`RequestTimeoutSeconds`，超时 504）  |

前后端分离（SPA）场景开跨域：

```csharp
builder.AddLedonWeb(o =>
{
    o.UseOpenApi = true;
    o.UseCors = true;
    o.CorsOrigins = ["https://app.example.com"];   // 空数组 = 允许任意来源（仅建议开发环境）
});
```

## 多实例部署：Data Protection 码环

多实例部署把 cookie/防伪令牌的加密码环持久化到 Redis（不配则各实例本地码环，负载均衡后会出现间歇性 400/登录态丢失）：

```csharp
builder.AddLedonWeb(o => o.DataProtectionRedisConnectionString = "localhost:6379");
```

应用标识默认取入口程序集名（多服务共享码环时用 `o.DataProtectionApplicationName` 对齐）。连接惰性建立 + `AbortOnConnectFail=false`——Redis 短暂不在线不阻塞启动。

## 验证

DataAnnotations 之外用 FluentValidation（`AddLedonFluentValidation()`，验证器自动发现，失败统一 400 ValidationProblemDetails）：

```csharp
public class EchoRequestValidator : AbstractValidator<EchoRequest>
{
    public EchoRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(50);
}
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

访问令牌过期后用 `IRefreshTokenService` 轮换（哈希存储 + 重放保护），详见[认证授权文档](docs/security.md)。不想自己写登录接口时 `app.MapLedonAuthEndpoints()` 提供标准 /auth/login、/auth/refresh 端点。

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
app.MapLedonLiveness();    // /health/live   进程在跑即 200
app.MapLedonReadiness();   // /health/ready  聚合全部检查
app.MapLedonStartup();     // /health/startup 宿主启动完成后 200（之前 503）
```

框架运行期指标（发件箱投递/重试/死信、队列成败、任务执行/错过触发）走 OTel Meter `Ledon.Queues`/`Ledon.Scheduling`，随 `AddLedonObservability()` 自动收集。

模块内可发自定义 span：`using var span = ctx.StartActivity("加载配置");`

## 弹性策略 / 配置 / 多租户

```csharp
builder.Services.AddLedonResilience();                    // 重试+超时+熔断，接管全部 HttpClient
builder.Services.AddLedonOptions<SmtpOptions>(builder.Configuration);   // 绑定+启动校验
builder.Services.AddLedonMultiTenancy();                  // 租户解析（请求头/查询串/claim/子域名）+ 后台穿透
app.UseLedonMultiTenancy();
```

## 云原生编排（Aspire）

```csharp
builder.AddLedonServiceDefaults();   // OTel（环境变量驱动）+ 健康检查 + 弹性 + 服务发现
app.MapLedonDefaultEndpoints();      // /health/live 与 /health/ready
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

协议栈与流媒体有独立示例（不依赖 Web，纯控制台即可跑通）：`./run.ps1 protocol`、`./run.ps1 stream`。

## 项目结构

```
src/Ledon.Core/         事件基元（IEventBus / IEventHandler）
src/Ledon.Module/       模块系统：契约、依赖图、运行时、约定扫描、程序集扫描
src/Ledon.Web/          Web 集成：端点、控制器、OpenAPI、API 规范化、关联 ID
src/Ledon.Data/         数据访问：ILedonDbContext、DbInitializer
src/Ledon.Caching/      缓存：内存（single-flight）/ 分布式（Redis）
src/Ledon.Queues/       后台队列 + 发件箱（Outbox）
src/Ledon.Scheduling/   定时任务（cron）
src/Ledon.Security/      认证授权（JWT/API Key/权限/刷新令牌）
src/Ledon.OpenIddict/     OIDC 服务器（标准 /connect/token）
src/Ledon.Observability OpenTelemetry + 健康检查
src/Ledon.Resilience/   弹性策略（重试/超时/熔断）
src/Ledon.Configuration/ 强类型配置约定
src/Ledon.Configuration.Nacos/ Nacos 配置中心接入
src/Ledon.Configuration.Apollo/ Apollo 配置中心接入
src/Ledon.Aspire/        云原生服务默认值（Aspire）
src/Ledon.SignalR/       SignalR Hub 自动发现
src/Ledon.MultiTenancy/ 多租户
src/Ledon.Testing/      集成测试基建
src/Ledon.Serilog/       Serilog 结构化日志集成
src/Ledon.FluentValidation/ FluentValidation 验证器自动发现
src/Ledon.Queues.RabbitMQ/ RabbitMQ 传输（发件箱 ↔ broker）
src/Ledon.Queues.Kafka/    Kafka 传输（发件箱 ↔ broker）
src/Ledon.Grpc/            gRPC 服务间通信（自动发现 + 约定映射 + 客户端）
src/Ledon.Workflow/        工作流引擎（步骤编排 + 重试 + Saga 补偿 + 重启恢复）
src/Ledon.Transactions/    统一事务入口（IUnitOfWork + Saga，本地/分布式同一种写法）
src/Ledon.Locking/         分布式锁（进程内 / Redis 互斥）
src/Ledon.FeatureManagement/ 功能开关与灰度（白名单 + 百分比 + 配置热更新）
src/Ledon.BlobStorage/     文件/对象存储抽象（本地/内存/S3 兼容，租户目录隔离）
src/Ledon.Notifications/   通知发送（邮件/短信/推送统一入口 + 重试 + 记录）
src/Ledon.Localization/    多语言与请求文化解析
src/Ledon.AuditLog/        操作审计流水（属性级 before/after）
src/Ledon.Application/     应用层管线（ICommand/IQuery + 验证/日志/事务/缓存）
src/Ledon.Admin/           统一运维端点（队列/死信/工作流/定时任务总览）
src/Ledon.Notifications.Feishu/ 飞书：通知渠道 + 事件回调（校验/解密/分发）
src/Ledon.Protocols/        协议解析：帧编解码 + 结构体映射（零依赖，高性能）
src/Ledon.Sockets.Client/   Socket 客户端：连接/断线指数退避重连/类型化收发
src/Ledon.Sockets.Server/   Socket 服务端：监听/发送串行/空闲超时/连接上限/类型化分发
src/Ledon.Sip/              SIP/SDP 协议解析（纯解析，obs-fold + 多值头 + 类型化头）
src/Ledon.Sip.Server/       SIP 服务端（基于 Socket 库，UDP/TCP + 事务层）
src/Ledon.Sip.Client/       SIP 客户端（基于 Socket 库，事务 + 对话 + ACK/CANCEL）
src/Ledon.Streaming/       流媒体：独立 RTMP 服务端（保活/鉴权/过期回收）+ HTTP-FLV/HLS + 推拉流鉴权/事件/踢人
src/Ledon.Webhooks/        出站 Webhook：订阅 + HMAC 签名 + 重试/死信/重放
src/Ledon.Notifications.Sms/  短信渠道：Webhook / Twilio
src/Ledon.Notifications.Push/ 推送渠道：Webhook / FCM
src/Ledon.Ids/           分布式 ID：ULID / GUID v7 / Snowflake
src/Ledon.Data.Seeding/  模块化数据播种（IDataSeeder + Order）
src/Ledon.Settings/      运行时设置（强类型 + 加密 + JSON 持久化）
src/Ledon.Search/        搜索（内存 / Meilisearch + 租户过滤）
src/Ledon.Security.ExternalLogins/ 外部登录（OIDC / GitHub）
src/Ledon.MultiTenancy.Management/ 租户目录管理
src/Ledon.Data.Encryption/ 列级透明加密
src/Ledon.Testing.Containers/ 集成测试容器夹具（Redis/PG/RabbitMQ/Kafka/MinIO）
src/Ledon.Security.ApiKeys/ 动态 API Key
src/Ledon.Security.TokenStores/ 共享刷新令牌存储
src/Ledon.Quotas/        固定窗口配额/限流（多实例缓存）
src/Ledon.Data.MultiTenancy/ 租户数据库路由与迁移
src/Ledon.Security.Totp/ TOTP 双因素与恢复码
src/Ledon.Security.Accounts/ 账号注册登录与密码生命周期
src/Ledon.ApiClients/    出站 HTTP 客户端（令牌缓存/关联 ID）
src/Ledon.Secrets/       Vault 密钥与配置源
src/Ledon.DataExchange/  CSV/Excel 导入导出
src/Ledon.Imaging/       图像处理（缩放/缩略图/格式转换）
src/Ledon.Search.Sync/   搜索索引增量同步与重试
src/Ledon.Data.Migrations/ 模块迁移编排与状态
src/Ledon.Idempotency/   幂等/防重复提交（指纹 + 重放 + 冲突）
src/Ledon.Security.Authorization/ RBAC 角色与权限
src/Ledon.ApiVersioning/ API 版本治理与弃用头
samples/Modules/        示例业务模块（Contracts / Users / Notifications / Api）
samples/                Ledon.Sample（控制台）/ Ledon.WebSample（Web）/ Ledon.WorkerSample（工作进程）
                        Ledon.CapabilitiesDemo（零依赖能力演示）/ Ledon.RbacApiDemo（RBAC API）/ Ledon.DataDemo（SQLite 多租户）
                        Ledon.ConfigCenterDemo（项目/环境/配置版本/成员权限的配置中心）
                        Ledon.ProtocolSample（协议栈：结构体映射→帧→TCP/UDP→SIP）
                        Ledon.StreamSample（流媒体：RTMP 推流→HTTP-FLV/HLS 播放）
tests/                  xunit 测试（模块 26 + Web 590 用例，顺序断言 + 变异验证）
tests/Ledon.Benchmarks/ 基准测试（BenchmarkDotNet：扫描/约定注入/事件/模块生命周期）
scripts/                任务编排：./run.ps1 [scan|build|test|sample|web|worker|check|bench|docs]
docs/                   文档站（docfx）
```

## 常用命令

```powershell
./run.ps1 check    # scan → build → test
./run.ps1 docs     # 构建文档站（docfx，产物 _site/）
./run.ps1 bench    # 基准测试（BenchmarkDotNet，Release）
./run.ps1 web      # Web 示例（多程序集 + 全功能链路）
./run.ps1 worker   # 工作进程示例（跨程序集事件解耦）
./run.ps1 sample   # 控制台零配置示例
./run.ps1 capabilities  # 零依赖能力演示（ID/TOTP/密钥/配额/RBAC/CSV）
./run.ps1 rbac     # RBAC API 示例（JWT + RBAC + 幂等 + 配额）
./run.ps1 data     # 数据示例（SQLite + 多租户 + 播种 + CSV）
./run.ps1 config   # 配置中心示例（项目/环境/版本/权限/成员管理）
./run.ps1          # 列出所有任务
```

模板：`dotnet new ledon-web`（Web 应用）、`dotnet new ledon-worker`（工作进程）、
`dotnet new ledon-module`（业务模块）、`dotnet new ledon-workflow`（工作流定义与步骤）、
`dotnet new ledon-react`（React + Vite + 内置 RBAC）、`dotnet new ledon-blazor`（Blazor Web App + 内置 RBAC）。

## 发布

CI（`.github/workflows/ci.yml`）在 push/PR 时 build+test；打 `v*` tag 时按版本打包并推 NuGet.org（需仓库 secret `NUGET_API_KEY`）。本地打包：`dotnet pack -c Release`。

每次推送到 main 还会跑 `Preview Packages` 工作流：产出 `{VersionPrefix}-preview.{run_number}` 的预览包并作为构件保留 14 天（不推 NuGet.org，不污染正式源）。

## 可靠投递：收件箱去重 + 死信管理
## 统一事务入口：本地/分布式同一种写法

`IUnitOfWork` 封装事务生命周期，用户不写 `BeginTransaction`/`Commit`/回滚样板：

```csharp
public class OrderService(IUnitOfWork uow)
{
    public async Task CreateAsync(CreateOrder cmd, CancellationToken ct) =>
        await uow.ExecuteAsync(async token =>
        {
            var order = new Order(cmd);
            order.Create();                                     // 领域事件（提交后才分发）
            uow.AddIntegrationEvent(new OrderPlaced(order.Id));  // 跨服务事件
            await uow.SaveChangesAsync(token);
        }, ct);
}
```

- **本地事务**：自动开事务/保存/提交，异常自动回滚；领域事件提交成功后才分发；
- **集成事件一种写法两种形态**：配 `AddLedonOutbox()` → 与业务数据原子落库、可靠投递（可跨服务）；没配 → 提交后进程内分发；
- **分布式（Saga）**：跨服务长流程每步独立事务、失败逆序补偿：

```csharp
var saga = provider.GetRequiredService<ISagaFactory>();
await saga.For("order-fulfillment")
    .Step("reserve", async (ctx, ct) => { /* 占库存 */ }, async (ctx, ct) => { /* 释放 */ })
    .Step("charge",  async (ctx, ct) => { /* 扣款   */ }, async (ctx, ct) => { /* 退款 */ })
    .RunAsync(ct);
```

详见[统一事务入口文档](docs/transactions.md)。
消息中间件是至少一次投递。收件箱把重复投递收敛为恰好一次处理；发件箱维护保证表不无限增长、搁置消息有人管：

```csharp
builder.Services.AddLedonInbox();   // 注册后：重复投递跳过、崩溃残留的陈旧认领超时后可重试

builder.Services.AddLedonOutbox(o =>
{
    o.Retention = TimeSpan.FromDays(7);    // 已处理消息保留时长（调度器定期清理）
    o.CleanupInterval = TimeSpan.FromHours(1);
});

// 死信（重试超上限）查询与人工重放：
var dead = await store.GetDeadLettersAsync(50, ct);   // IOutboxStore
await store.ReviveAsync(dead[0].Id, ct);              // 清零重试计数，重新投递
```

## gRPC 服务间通信

```csharp
builder.Services.AddLedonGrpc();     // 自动发现 gRPC 服务实现类（[BindServiceMethod] 判据）
app.MapLedonGrpcServices();          // 约定映射（幂等）

builder.Services.AddLedonGrpcClient<Greeter.GreeterClient>("https://localhost:7001");   // 强类型客户端
```

## 缓存扩展与混合两级缓存

```csharp
builder.Services.AddLedonHybridCaching();   // L1 内存 + L2 分布式（.NET 10 HybridCache）

cache.SetAsync("product:1", product, expiration: TimeSpan.FromMinutes(10), ct);
await cache.RemoveByPrefixAsync("product:", ct);   // 前缀删除（分布式经 SCAN / 混合经标签）
cache.Set("session:abc", state, TimeSpan.FromHours(1), slidingExpiration: TimeSpan.FromMinutes(20));
```

## 数据层约定：审计字段与软删除

```csharp
public class Order : IAudited { /* CreatedAt/CreatedBy/ModifiedAt/ModifiedBy 自动填充 */ }
public class Customer : IAudited, ISoftDelete { /* 删除自动转换为 IsDeleted = true */ }

builder.Services.AddLedonData(o => o.EnableAuditing = true);   // 或 o.EnableRetryOnFailure = true（连接弹性）
// 查询过滤显式启用：modelBuilder.ApplySoftDeleteFilters();
```

## 定时任务：执行历史与手动触发

```csharp
var history = provider.GetRequiredService<IScheduledTaskExecutionHistory>();
history.Latest(nameof(ReconcileTask));     // TaskName/耗时/成败/是否手动

var invoker = provider.GetRequiredService<ISchedulerInvoker>();
await invoker.InvokeAsync<ReconcileTask>(ct);   // 等不到 cron 立即重跑
```

## 许可证

[MIT](LICENSE) © wosledon

## 设计边界（v1 明确不做）

- 运行时动态加载 / 插件热插拔（当前为静态引用；发现环节已与加载方式解耦，后续可扩展）
- 模块级独立 DI 容器（所有模块共享根容器）
- 每模块独立配置（配置经 `ModuleContext.Configuration` 走宿主配置即可）
- 多租户的隐式查询过滤（提供上下文与解析，隔离由查询层显式应用——约定显式，避免意外）
- NativeAOT/裁剪开箱支持：自动发现与约定注入基于反射，入口方法已标注 `[RequiresDynamicCode]` 给出裁剪警告；需要 AOT 时保留相关程序集/类型（rd.xml / DynamicDependency）或改显式注册
