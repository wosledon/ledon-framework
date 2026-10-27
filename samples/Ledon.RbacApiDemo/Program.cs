using Ledon.ApiVersioning;
using Ledon.Idempotency;
using Ledon.Module.Abstractions;
using Ledon.Quotas;
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
    options.Jwt.SigningKey = "ledon-rbac-demo-signing-key-0123456789";
});

builder.Services.AddLedonAuthorization(options =>
{
    options.BootstrapAdminUserIds.Add("admin");
});

builder.Services.AddLedonIdempotency(options => options.Ttl = TimeSpan.FromMinutes(10));
builder.Services.AddLedonQuotas(options =>
{
    options.DefaultLimit = 50;
    options.Window = TimeSpan.FromMinutes(1);
    options.KeySelector = context =>
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
});
builder.Services.AddLedonApiVersioning(options => options.UseUrlSegment = false);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var roles = scope.ServiceProvider.GetRequiredService<RoleAuthorizationService>();

    try
    {
        await roles.CreateRoleAsync("reader", ["weather.read", "orders.write"]);
    }
    catch (InvalidOperationException)
    {
        // 幂等：持久化存储或重复启动
    }

    await roles.AssignRoleAsync("user", "reader");
}

// 路由之后执行；UseLedonWeb 内含 UseRouting / 认证 / 授权
app.UseLedonWeb();
app.UseLedonQuotas();
app.UseLedonIdempotency();
app.UseLedonApiVersioning();
app.MapLedonAuthEndpoints();

app.MapGet("/api/public", () => new
{
    message = "公开接口",
    demoAccounts = new[] { "admin/admin123", "user/user123" },
});

app.MapGet("/api/weather", () => new[]
{
    new { city = "Shanghai", temperature = 24, summary = "晴" },
    new { city = "Beijing", temperature = 18, summary = "多云" },
}).RequireAuthorization("weather.read");

var orders = new List<DemoOrder>();
app.MapPost("/api/orders", (CreateOrderRequest request, HttpContext context) =>
{
    // Idempotency-Key 由中间件按请求指纹重放，这里只处理首次请求
    var order = new DemoOrder(
        Guid.NewGuid(),
        request.Product,
        request.Amount,
        context.User.Identity?.Name ?? "unknown",
        DateTimeOffset.UtcNow);

    lock (orders)
        orders.Add(order);

    return Results.Created($"/api/orders/{order.Id}", order);
}).RequireAuthorization("orders.write");

app.MapGet("/api/orders", () =>
{
    lock (orders)
        return Results.Ok(orders);
}).RequireAuthorization("orders.write");

app.MapGet("/api/admin", () => new { message = "只有 admin 角色可见" })
    .RequireAuthorization("authorization.manage");

app.MapGet("/api/version", () => new
{
    api = "v1",
    hint = "请求头 X-Api-Version 可演示版本读取（当前端点不限制版本）",
}).WithMetadata(new ApiDeprecatedAttribute(link: "https://github.com/wosledon/ledon-framework"));

app.Run();

public sealed record CreateOrderRequest(string Product, decimal Amount);

public sealed record DemoOrder(
    Guid Id,
    string Product,
    decimal Amount,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

/// <summary>开发演示账号；生产替换为真实用户存储。</summary>
public sealed class DemoUserValidator : ILedonUserValidator
{
    public Task<LedonUser?> ValidateAsync(string userName, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult<LedonUser?>(userName.Trim().ToLowerInvariant() switch
        {
            "admin" when password == "admin123" => new LedonUser("admin", "admin", []),
            "user" when password == "user123" => new LedonUser("user", "user", []),
            _ => null,
        });

    public Task<LedonUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<LedonUser?>(userId switch
        {
            "admin" => new LedonUser("admin", "admin", []),
            "user" => new LedonUser("user", "user", []),
            _ => null,
        });
}

/// <summary>Ledon 模块系统需要至少一个模块。</summary>
public sealed class DemoModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
