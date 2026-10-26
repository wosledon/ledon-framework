using System.Net.Http.Json;
using System.Security.Claims;
using Ledon.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>RBAC：角色权限、通配符、缓存失效与授权管线。</summary>
public class AuthorizationTests
{
    [Fact]
    public async Task Role_permissions_support_wildcards_and_assignment_removal()
    {
        await using var provider = BuildProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();

        await service.CreateRoleAsync("support", ["orders.*", "users.read"]);
        await service.AssignRoleAsync("user-1", "support");

        Assert.True(await service.HasPermissionAsync("user-1", "orders.read"));
        Assert.True(await service.HasPermissionAsync("user-1", "orders.create"));
        Assert.True(await service.HasPermissionAsync("user-1", "users.read"));
        Assert.False(await service.HasPermissionAsync("user-1", "users.write"));
        Assert.True(CachingPermissionEvaluator.Matches("orders.*", "orders.create"));

        Assert.True(await service.RemoveRoleAsync("user-1", "support"));
        Assert.False(await service.HasPermissionAsync("user-1", "orders.read"));
    }

    [Fact]
    public async Task Permission_cache_is_invalidated_when_role_permissions_change()
    {
        await using var provider = BuildProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();

        await service.CreateRoleAsync("reader", []);
        await service.AssignRoleAsync("user-2", "reader");

        Assert.False(await service.HasPermissionAsync("user-2", "reports.read"));

        await service.SetPermissionsAsync("reader", ["reports.read"]);

        Assert.True(await service.HasPermissionAsync("user-2", "reports.read"));
    }

    [Fact]
    public async Task Authorization_pipeline_uses_role_permissions()
    {
        await using var provider = BuildProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var user = Principal("user-3");

        var denied = await authorization.AuthorizeAsync(user, resource: null, policyName: "orders.read");
        Assert.False(denied.Succeeded);

        await service.CreateRoleAsync("operator", ["orders.*"]);
        await service.AssignRoleAsync("user-3", "operator");

        var allowed = await authorization.AuthorizeAsync(user, resource: null, policyName: "orders.read");
        Assert.True(allowed.Succeeded);
    }

    [Fact]
    public async Task Role_assignment_can_be_scoped_to_tenant()
    {
        await using var provider = BuildProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();

        await service.CreateRoleAsync("tenant-reader", ["reports.read"]);
        await service.AssignRoleAsync("user-4", "tenant-reader", "tenant-a");

        Assert.True(await service.HasPermissionAsync("user-4", "reports.read", "tenant-a"));
        Assert.False(await service.HasPermissionAsync("user-4", "reports.read", "tenant-b"));
        Assert.False(await service.HasPermissionAsync("user-4", "reports.read"));
    }

    [Fact]
    public async Task Management_endpoints_can_be_mapped_and_used()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonAuthorization(o =>
        {
            o.MapEndpoints = true;
            o.RequireAuthorization = false;
        });

        await using var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();

        var client = app.GetTestClient();
        var create = await client.PostAsJsonAsync(
            "/auth/authorization/roles",
            new { name = "auditor", permissions = new[] { "audit.read" } });

        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);

        var roles = await client.GetFromJsonAsync<AuthorizationRole[]>("/auth/authorization/roles");
        Assert.Contains(roles!, role => role.Name == "auditor");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonAuthorization(o => o.CacheDuration = TimeSpan.FromMinutes(1));

        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            authenticationType: "test"));
}
