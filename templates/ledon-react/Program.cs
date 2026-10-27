using Ledon.Module.Abstractions;
using Ledon.Security;
using Ledon.Security.Authorization;
using Ledon.Web;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<ILedonUserValidator, DemoUserValidator>();

builder.AddLedonWeb(options =>
{
    options.UseOpenApi = true;
    options.UseScalarUi = true;
    options.UseResponseCompression = true;
});

builder.Services.AddLedonSecurity(options =>
{
    options.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
        ?? "ledon-react-development-signing-key-change-me";
});

builder.Services.AddLedonAuthorization(options =>
{
    // 首个管理员：登录用户 ID 为 admin 时自动获得内置 admin 角色（权限 *）。
    options.BootstrapAdminUserIds.Add("admin");
    options.MapEndpoints = true;
});

var app = builder.Build();

// 演示角色：reader 可读取天气，user 账号启动时分配 reader。
using (var scope = app.Services.CreateScope())
{
    var roles = scope.ServiceProvider.GetRequiredService<RoleAuthorizationService>();

    try
    {
        await roles.CreateRoleAsync("reader", ["weather.read"]);
    }
    catch (InvalidOperationException)
    {
        // 已存在（持久化存储或热重启）时保持幂等
    }

    await roles.AssignRoleAsync("user", "reader");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseLedonWeb();
app.MapLedonAuthEndpoints();

app.MapGet("/api/public", () => new { message = "公开接口" });

app.MapGet("/api/weather", () => new[]
{
    new { city = "Shanghai", temperature = 24, summary = "晴" },
    new { city = "Beijing", temperature = 18, summary = "多云" },
}).RequireAuthorization("weather.read");

app.MapGet("/api/admin", () => new { message = "只有 admin 角色可见" })
    .RequireAuthorization("authorization.manage");

app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Ledon 模块系统至少需要一个模块；业务模块可拆到独立程序集。</summary>
public sealed class AppModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>开发演示账号：admin/admin123（管理员）、user/user123（reader）。生产请替换为真实用户存储。</summary>
public sealed class DemoUserValidator : ILedonUserValidator
{
    public Task<LedonUser?> ValidateAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var user = userName.Trim().ToLowerInvariant() switch
        {
            "admin" when password == "admin123" => new LedonUser("admin", "admin", []),
            "user" when password == "user123" => new LedonUser("user", "user", []),
            _ => null,
        };

        return Task.FromResult(user);
    }

    public Task<LedonUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(userId switch
        {
            "admin" => new LedonUser("admin", "admin", []),
            "user" => new LedonUser("user", "user", []),
            _ => null,
        });
}
