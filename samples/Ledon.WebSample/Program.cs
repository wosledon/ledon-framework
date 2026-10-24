using Ledon.Data;
using Ledon.Web;
using Ledon.WebSample;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;

// 整个 Web 示例只有两行框架接入：
//   builder.AddLedonWeb(o => o.UseOpenApi = true);   // 模块系统 + 端点/控制器自动发现 + OpenAPI 按需
//   builder.Services.AddLedonData();                 // 数据访问按需引入（DbContext 自动注册 + 自动建库）
// 其余全部靠自动发现与自动注入。

const string url = "http://127.0.0.1:5180";

var builder = WebApplication.CreateBuilder(args);
builder.AddLedonWeb(o => o.UseOpenApi = true);
builder.Services.AddLedonData();
builder.WebHost.UseUrls(url);

var app = builder.Build();
app.UseLedonWeb();

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

Console.WriteLine("\n正在停止宿主（模块将按初始化逆序自动关闭）...");
await app.StopAsync();

static Task Show(string label, string body)
{
    Console.WriteLine($"{label}  →  {body}");
    return Task.CompletedTask;
}
