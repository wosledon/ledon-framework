using Ledon.Caching;
using Ledon.Data;
using Ledon.Observability;
using Ledon.Queues;
using Ledon.Scheduling;
using Ledon.Sample.Api;
using Ledon.Web;
using Ledon.WebSample;
using Ledon.WebSample.Grpc;
using Ledon.Workflow;
using Ledon.Transactions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;

// 整个 Web 示例只有几行框架接入，其余全部靠自动发现与自动注入：
//   builder.AddLedonWeb(o => ...);              // 模块系统 + 端点/控制器 + Web 中间件开关
//   builder.Services.AddLedonData();            // 数据访问（DbContext 自动注册 + 自动建库 + 审计）
//   builder.Services.AddLedonCaching();         // 缓存（按需）
//   builder.Services.AddLedonQueues();          // 后台队列（按需）
//   builder.Services.AddLedonObservability();   // OpenTelemetry 链路/指标（按需）
//   builder.Services.AddLedonHealthChecks();    // 健康检查自动发现（按需）
//   builder.Services.AddLedonScheduling();      // 定时任务 + 执行历史 + 手动触发（按需）
//   builder.Services.AddLedonSignalR();         // SignalR Hub 自动发现（按需）
//   builder.Services.AddLedonGrpc();            // gRPC 服务自动发现（按需）

const string url = "http://127.0.0.1:5180";
const string grpcUrl = "http://127.0.0.1:5181";   // 明文 h2c：gRPC 专用
const int restPort = 5180;
const int grpcPort = 5181;

var builder = WebApplication.CreateBuilder(args);
// 应用层的校验异常映射为 400（框架的 IExceptionMapping 扩展点，替代手写 IExceptionHandler）
builder.Services.AddLedonExceptionMapping<ValidationExceptionMapping>();

builder.AddLedonWeb(o =>
{
    o.UseOpenApi = true;
    o.UseIdempotency = true;              // 幂等请求（需 IDistributedCache，见下）
    o.UseRateLimiting = true;             // 限流：超限 429
    o.RateLimitPermitLimit = 1000;        // 示例窗口内不会触发，生产按需调小
    o.UseResponseCompression = true;      // Brotli/Gzip 压缩
    o.UseRequestTimeouts = true;          // 慢请求 504
    o.RequestTimeoutSeconds = 30;
});
builder.Services.AddDistributedMemoryCache();                 // 幂等中间件的存储（多实例换 Redis）
builder.Services.AddLedonData(o => o.EnableAuditing = true);   // 审计字段自动填充
builder.Services.AddLedonCaching();
builder.Services.AddLedonQueues();
builder.Services.AddLedonObservability(o => o.ServiceName = "ledon-web-sample");
builder.Services.AddLedonHealthChecks();
builder.Services.AddLedonScheduling();
builder.Services.AddLedonSignalR();
builder.Services.AddLedonGrpc();
builder.Services.AddLedonWorkflow();   // 工作流引擎（定义/步骤自动发现 + 重启恢复）
builder.Services.AddLedonTransactions();   // 统一事务入口（本地/分布式同一种写法）
builder.Services.AddLedonLocking();        // 分布式锁（进程内）
builder.Services.AddLedonFeatureManagement();   // 功能开关（读 Features 配置段）
builder.Services.AddLedonLocalization(o =>    // 多语言（查询串/Cookie/Accept-Language）
{
    o.DefaultCulture = "zh-CN";
    o.SupportedCultures.Add("en-US");
});
builder.Services.AddLedonNotifications(o =>    // 通知（未配 SMTP 时记录失败结果，不发真实邮件）
{
    o.FromAddress = "noreply@ledon.dev";
});
builder.Services.AddLedonApplication(o =>      // 应用层管线：验证 → 日志 → 事务
{
    o.UseValidation();
    o.UseLogging();
    o.UseTransactions();
});
builder.Services.AddLedonBlobStorage(o =>      // 文件存储（本地目录）
{
    o.RootPath = Path.Combine(AppContext.BaseDirectory, "App_Data", "blobs");
});
builder.Services.AddLedonAdmin();              // 统一运维端点 /admin/ops
// gRPC 需要 HTTP/2：示例主端口跑 HTTP/1.1（REST 演示），另开一个明文 h2c 端口给 gRPC。
// 显式 Listen 会覆盖 UseUrls，所以两个端口都在这里声明
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(restPort, listen =>
        listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenLocalhost(grpcPort, listen =>
        listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

var app = builder.Build();
app.UseLedonRequestLocalization();   // 请求文化（路由之前）
app.UseLedonWeb();
app.MapLedonHealthChecks();   // GET /health
app.MapLedonStartup();        // GET /health/startup（宿主启动完成后 200）
app.MapLedonSignalRHubs();    // DemoHub → /hubs/demo
app.MapLedonGrpcServices();   // Demo.Demo → gRPC 服务

// 每次运行都是干净起点
if (File.Exists(DbFile.Path))
    File.Delete(DbFile.Path);

await app.StartAsync();
Console.WriteLine($"宿主已启动：{url}（模块已自动初始化，日志在下面）\n");

using var client = new HttpClient();
await Show("GET  /hello", await client.GetStringAsync($"{url}/hello"));
await Show("GET  /api/todos", await client.GetStringAsync($"{url}/api/todos"));
var created = await client.PostAsJsonAsync($"{url}/api/todos", new TodoItem { Title = "体验 Ledon" });
await Show("POST /api/todos", $"HTTP {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
await Show("GET  /api/todos", await client.GetStringAsync($"{url}/api/todos"));
var openApi = await client.GetAsync($"{url}/openapi/v1.json");
await Show("GET  /openapi/v1.json", $"HTTP {(int)openApi.StatusCode}");
await Show("GET  /health", await client.GetStringAsync($"{url}/health"));
await Show("GET  /health/startup", $"HTTP {(int)(await client.GetAsync($"{url}/health/startup")).StatusCode}");

// 幂等：带 Idempotency-Key 的重复 POST 只执行一次（第二次返回缓存响应）
var idemKey = Guid.NewGuid().ToString();
HttpRequestMessage IdemRequest() => new(HttpMethod.Post, $"{url}/api/todos")
{
    Content = JsonContent.Create(new TodoItem { Title = "幂等演示" }),
};
var idemFirst = IdemRequest();
idemFirst.Headers.Add("Idempotency-Key", idemKey);
var idemReplay = IdemRequest();
idemReplay.Headers.Add("Idempotency-Key", idemKey);
var first = await client.SendAsync(idemFirst);
var replay = await client.SendAsync(idemReplay);
await Show("IDEMPOTENCY",
    $"首次 HTTP {(int)first.StatusCode}，重放 HTTP {(int)replay.StatusCode}" +
    $"（Idempotency-Replayed: {replay.Headers.TryGetValues("Idempotency-Replayed", out _)}）");

// 响应压缩：Accept-Encoding: br 时响应带 Content-Encoding
var compressed = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/todos");
compressed.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
var compressedResponse = await client.SendAsync(compressed);
await Show("COMPRESSION",
    $"Content-Encoding: {string.Join(",", compressedResponse.Content.Headers.ContentEncoding)}");

// 缓存：同一 key 并发只回源一次（single-flight）
var cache = app.Services.GetRequiredService<ICacheService>();
var cacheHits = 0;
async Task<string> LoadAsync(CancellationToken ct)
{
    Interlocked.Increment(ref cacheHits);
    await Task.Delay(30, ct);
    return $"回源结果 x{cacheHits}";
}
var cached = await Task.WhenAll(
    cache.GetOrCreateAsync("demo", LoadAsync),
    cache.GetOrCreateAsync("demo", LoadAsync));
await Show("CACHE demo", $"{cached[0]}（回源次数 {cacheHits}）");

// 缓存：异步写入 + 前缀删除
await cache.SetAsync("demo:notes:1", "n1");
await cache.SetAsync("demo:notes:2", "n2");
await cache.SetAsync("demo:other", "o");
await cache.RemoveByPrefixAsync("demo:notes:");
var otherKept = cache.TryGet<string>("demo:other", out var other) ? other : "没了";
await Show("CACHE prefix", $"删除 demo:notes:* 后 demo:other 仍在：{otherKept}");

// 后台队列：丢一个任务，worker 在独立作用域里执行
var queue = app.Services.GetRequiredService<IBackgroundQueue>();
var jobDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await queue.EnqueueAsync(async (scope, ct) =>
{
    var logger = scope.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("后台任务执行中（独立 DI 作用域）");
    jobDone.TrySetResult();
    await Task.CompletedTask;
});
await jobDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("QUEUE 后台任务   →  已完成");

// 审计 + 软删除：时间戳框架自动填，删除转更新（查询过滤隐藏）
var note = await client.PostAsJsonAsync($"{url}/api/notes", new Note { Text = "审计演示" });
var createdNote = await note.Content.ReadFromJsonAsync<Note>();
await Show("AUDIT", $"CreatedAt={createdNote?.CreatedAt:HH:mm:ss}（框架自动填充）");
var deleteResult = await client.DeleteAsync($"{url}/api/notes/{createdNote?.Id}");
await Show("SOFT DELETE",
    $"删除 HTTP {(int)deleteResult.StatusCode}，再查列表：{await client.GetStringAsync($"{url}/api/notes")}（行不出库，被过滤隐藏）");

// 定时任务：cron 自动执行 + 执行历史 + 手动触发
var tickTask = app.Services.GetServices<IScheduledTask>().OfType<DemoTickTask>().Single();
await WaitUntil(() => tickTask.Ticks > 0, TimeSpan.FromSeconds(5));
await Show("SCHEDULE", $"DemoTickTask 已按 cron 执行 {tickTask.Ticks} 次");

var history = app.Services.GetRequiredService<IScheduledTaskExecutionHistory>();
var invoker = app.Services.GetRequiredService<ISchedulerInvoker>();
var before = tickTask.Ticks;
await invoker.InvokeAsync<DemoTickTask>(CancellationToken.None);
var latest = history.Latest(nameof(DemoTickTask));
await Show("SCHEDULE invoke",
    $"手动触发一次：{before} → {tickTask.Ticks}；历史最近一条：成功={latest?.Success}（{latest?.Duration.TotalMilliseconds:F0}ms，手动={latest?.Manual}）");
await Show("SCHEDULE api",
    $"POST /admin/tasks/DemoTickTask/run → HTTP {(int)(await client.PostAsync($"{url}/admin/tasks/DemoTickTask/run", null)).StatusCode}");

// SignalR：Hub 自动发现并映射（协商端点应答即证明已映射）
var negotiate = await client.PostAsync($"{url}/hubs/demo/negotiate", null);
await Show("SIGNALR", $"POST /hubs/demo/negotiate → HTTP {(int)negotiate.StatusCode}");

// gRPC：服务自动发现 + 约定映射，客户端强类型调用（h2c 端口）
using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(grpcUrl);
var demo = new Demo.DemoClient(channel);
var reply = await demo.PingAsync(new PingRequest { Message = "Ledon" });
await Show("GRPC", $"Demo.Ping → {reply.Reply}");

// gRPC 标准健康检查服务（grpc.health.v1）：负载均衡/K8s 探针可直接用
var health = new Grpc.Health.V1.Health.HealthClient(channel);
var healthReply = await health.CheckAsync(new Grpc.Health.V1.HealthCheckRequest { Service = "" });
await Show("GRPC health", $"grpc.health.v1.Health/Check → {healthReply.Status}");

// 工作流引擎：创建实例 → 运行到完成（步骤/定义自动发现）
var engine = app.Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngine>();
var orderFlow = await engine.CreateAsync("demo-order", new Dictionary<string, object?> { ["orderId"] = "O-1001" });
var finished = await engine.RunAsync(orderFlow.Id, CancellationToken.None);
await Show("WORKFLOW", $"demo-order 实例 {finished.Id.ToString()[..8]} → {finished.Status}（步骤：{string.Join(" → ", finished.CompletedSteps)}）");

// 统一事务入口：本地事务 + 集成事件（一种写法；此处未配发件箱 → 提交后进程内分发）
var accountService = app.Services.CreateScope().ServiceProvider.GetRequiredService<IAccountService>();
await accountService.OpenAsync("alice", CancellationToken.None);
await Show("TRANSACTION", "AccountService.OpenAsync 经 IUnitOfWork 提交（领域/集成事件自动分发）");

// Saga：步骤失败逆序补偿（ship 步骤故意失败）
var saga = app.Services.CreateScope().ServiceProvider.GetRequiredService<ISagaFactory>();
try
{
    await saga.For("demo-saga")
        .Step("reserve", (ctx, ct) => Task.CompletedTask, compensate: (ctx, ct) => Task.CompletedTask)
        .Step("ship", (ctx, ct) => throw new InvalidOperationException("no-stock"))
        .RunAsync();
}
catch (SagaExecutionException ex)
{
    await Show("SAGA", $"步骤 {ex.Step} 失败 → 已逆序补偿（SagaExecutionException）");
}
// 平台服务：CQRS 管线（验证 → 日志 → 事务）+ 功能开关 + 分布式锁 + 运维端点
var orderClient = new HttpClient { BaseAddress = new Uri(url) };
var createdOrder = await orderClient.PostAsJsonAsync("/demo/orders", new { sku = "SKU-1", qty = 2 });
await Show("CQRS", $"POST /demo/orders → HTTP {(int)createdOrder.StatusCode}（命令经验证/日志/事务管线）");

var rejected = await orderClient.PostAsJsonAsync("/demo/orders", new { sku = "SKU-1", qty = 0 });
await Show("CQRS", $"POST /demo/orders(qty=0) → HTTP {(int)rejected.StatusCode}（校验行为拦截，不进处理器）");

var beta = await orderClient.GetAsync("/demo/beta");
await Show("FEATURE", $"GET /demo/beta → HTTP {(int)beta.StatusCode}（开关未启用 = 404）");

var paid = await orderClient.PostAsync("/demo/pay/O-1001", null);
await Show("LOCK", $"POST /demo/pay/O-1001 → HTTP {(int)paid.StatusCode}（分布式锁串行化同一订单）");

var ops = await orderClient.GetAsync("/admin/ops/summary");
await Show("ADMIN", $"GET /admin/ops/summary → HTTP {(int)ops.StatusCode}（发件箱/队列/工作流/调度一页总览）");

Console.WriteLine("\n正在停止宿主（模块将按初始化逆序自动关闭）...");
await app.StopAsync();

static Task Show(string label, string body)
{
    Console.WriteLine($"{label}  →  {body}");
    return Task.CompletedTask;
}

static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition() && DateTime.UtcNow < deadline)
        await Task.Delay(50);

    if (!condition())
        throw new TimeoutException("等待条件超时");
}

/// <summary>
/// 把应用层的 <c>ValidationException</c> 映射为 400 + 逐条错误。
/// 框架只提供 <c>IExceptionMapping</c> 扩展点，具体异常类型由使用方认领——
/// 这样 Ledon.Web 不必反向依赖 Ledon.Application。
/// </summary>
internal sealed class ValidationExceptionMapping : Ledon.Web.ApiConventions.IExceptionMapping
{
    public bool TryMap(Exception exception, out int statusCode, out string title, out string? detail)
    {
        if (exception is not Ledon.Application.ValidationException validation)
        {
            statusCode = 0;
            title = "";
            detail = null;

            return false;
        }

        statusCode = Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest;
        title = "请求校验失败";
        detail = validation.Errors.Count > 0
            ? string.Join("; ", validation.Errors)
            : validation.Message;

        return true;
    }
}
