using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 两行接入：模块系统 + 端点/控制器自动发现 + API 规范化
var builder = WebApplication.CreateBuilder(args);
builder.AddLedonWeb(o => o.UseOpenApi = true);

var app = builder.Build();
app.UseLedonWeb();
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
