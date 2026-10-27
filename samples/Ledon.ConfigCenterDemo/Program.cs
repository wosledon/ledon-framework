using System.Text.Json.Serialization;
using Ledon.ConfigCenterDemo;
using Ledon.Module.Abstractions;
using Ledon.Security;
using Ledon.Security.Authorization;
using Ledon.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
var database = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "config-center.db");
Directory.CreateDirectory(Path.GetDirectoryName(database)!);

builder.Services.AddDbContext<ConfigCenterDbContext>(options => options.UseSqlite($"Data Source={database}"));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.AddLedonWeb(options =>
{
    options.UseOpenApi = true;
    options.UseScalarUi = true;
});

builder.Services.AddScoped<ILedonUserValidator, DemoUserValidator>();
builder.Services.AddLedonSecurity(options =>
{
    options.Jwt.SigningKey = "ledon-config-center-demo-signing-key-0123456789";
});
builder.Services.AddLedonAuthorization(options =>
{
    options.BootstrapAdminUserIds.Add("admin");
});
builder.Services.AddScoped<ProjectAuthorizationService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ConfigCenterDbContext>();
    await db.Database.EnsureCreatedAsync();
    await ConfigCenterSeeder.SeedAsync(db, app.Logger);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseLedonWeb();
app.MapLedonAuthEndpoints();
ConfigCenterEndpoints.Map(app);
app.MapFallbackToFile("index.html");

app.Run();

public sealed class DemoModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>开发演示账号；生产替换为真实用户存储。</summary>
public sealed class DemoUserValidator : ILedonUserValidator
{
    public Task<LedonUser?> ValidateAsync(string userName, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult<LedonUser?>(userName.Trim().ToLowerInvariant() switch
        {
            "admin" when password == "admin123" => new LedonUser("admin", "admin", []),
            "user" when password == "user123" => new LedonUser("user", "user", []),
            "viewer" when password == "viewer123" => new LedonUser("viewer", "viewer", []),
            _ => null,
        });

    public Task<LedonUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<LedonUser?>(userId switch
        {
            "admin" => new LedonUser("admin", "admin", []),
            "user" => new LedonUser("user", "user", []),
            "viewer" => new LedonUser("viewer", "viewer", []),
            _ => null,
        });
}
