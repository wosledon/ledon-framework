using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 两行接入：模块系统 + 端点/控制器自动发现 + API 规范化
// 其余能力（缓存/队列/调度/认证/可观测性/SignalR/gRPC……）用哪个引哪个：
//   builder.Services.AddLedonCaching();        // 缓存（single-flight / 混合两级）
//   builder.Services.AddLedonQueues();         // 后台队列
//   builder.Services.AddLedonScheduling();     // 定时任务 + 执行历史 + 手动触发
//   builder.Services.AddLedonSecurity(...);    // JWT + API Key + 权限策略
//   builder.Services.AddLedonObservability();  // OTel 链路/指标 + 健康检查
//   builder.Services.AddLedonSignalR();        // Hub 自动发现（多实例可配 Redis backplane）
//   builder.Services.AddLedonGrpc();           // gRPC 服务自动发现
var builder = WebApplication.CreateBuilder(args);
builder.AddLedonWeb(o =>
{
    o.UseOpenApi = true;
    o.UseScalarUi = true;            // 文档 UI（/scalar）
    o.UseRateLimiting = true;        // 限流（超限 429）
    o.UseResponseCompression = true; // Brotli/Gzip 压缩
    o.UseRequestTimeouts = true;     // 慢请求 504
});

var app = builder.Build();
app.UseLedonWeb();
app.MapLedonLiveness();   // /health/live（需 AddLedonHealthChecks）
app.MapLedonReadiness();  // /health/ready
app.Run();

// ---------- 模块 ----------

public class AppModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("AppModule 初始化");
        return Task.CompletedTask;
    }
}

// ---------- 端点（实现 IEndpoint 即自动映射） ----------

public class HelloEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder) =>
        builder.MapGet("/hello", () => "Hello, Ledon!");
}
