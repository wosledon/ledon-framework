using Ledon.Data;
using Ledon.Caching;
using Ledon.Observability;
using Ledon.Queues;
using Ledon.Sample.Api;
using Ledon.Web;
using Ledon.WebSample;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

// 整个 Web 示例只有几行框架接入，其余全部靠自动发现与自动注入：
//   builder.AddLedonWeb(o => o.UseOpenApi = true);   // 模块系统 + 端点/控制器自动发现 + OpenAPI 按需
//   builder.Services.AddLedonData();                 // 数据访问（DbContext 自动注册 + 自动建库）
//   builder.Services.AddLedonCaching();              // 缓存（按需）
//   builder.Services.AddLedonQueues();               // 后台队列（按需）
//   builder.Services.AddLedonObservability();        // OpenTelemetry 链路/指标（按需）
//   builder.Services.AddLedonHealthChecks();         // 健康检查自动发现（按需）

const string url = "http://127.0.0.1:5180";

var builder = WebApplication.CreateBuilder(args);
builder.AddLedonWeb(o => o.UseOpenApi = true);
builder.Services.AddLedonData();
builder.Services.AddLedonCaching();
builder.Services.AddLedonQueues();
builder.Services.AddLedonObservability(o => o.ServiceName = "ledon-web-sample");
builder.Services.AddLedonHealthChecks();
builder.WebHost.UseUrls(url);

var app = builder.Build();
app.UseLedonWeb();
app.MapLedonHealthChecks();   // GET /health

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

// 后台队列：丢一个任务，worker 在独立作用域里执行
var queue = app.Services.GetRequiredService<IBackgroundQueue>();
var jobDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await queue.EnqueueAsync(async (scope, ct) =>
{
    var logger = scope.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    logger.LogInformation("后台任务执行中（独立 DI 作用域）");
    jobDone.TrySetResult();
    await Task.CompletedTask;
});
await jobDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("QUEUE 后台任务   →  已完成");

Console.WriteLine("\n正在停止宿主（模块将按初始化逆序自动关闭）...");
await app.StopAsync();

static Task Show(string label, string body)
{
    Console.WriteLine($"{label}  →  {body}");
    return Task.CompletedTask;
}
