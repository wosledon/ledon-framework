using System.Security.Claims;
using Ledon.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>RBAC 边界：删除角色、自定义策略、租户 claim、重复/非法输入。</summary>
public class AuthorizationEdgeTests
{
    [Fact]
    public async Task Deleting_role_clears_assignments_and_permissions()
    {
        await using var provider = BuildProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();

        await service.CreateRoleAsync("temporary", ["reports.read"]);
        await service.AssignRoleAsync("user-1", "temporary");

        Assert.True(await service.DeleteRoleAsync("temporary"));
        Assert.Empty(await service.GetUserRolesAsync("user-1"));
        Assert.False(await service.HasPermissionAsync("user-1", "reports.read"));
        Assert.False(await service.DeleteRoleAsync("temporary"));
    }

    [Fact]
    public async Task Custom_authorization_policy_is_preserved()
    {
        await using var provider = BuildProvider(services =>
            services.AddAuthorization(options => options.AddPolicy(
                "scope-api",
                policy => policy.RequireClaim("scope", "api"))));

        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var allowed = await authorization.AuthorizeAsync(
            Principal("user-2", new Claim("scope", "api")),
            resource: null,
            policyName: "scope-api");
        var denied = await authorization.AuthorizeAsync(
            Principal("user-2"),
            resource: null,
            policyName: "scope-api");

        Assert.True(allowed.Succeeded);
        Assert.False(denied.Succeeded);
    }

    [Fact]
    public async Task Tenant_claim_scopes_role_authorization()
    {
        await using var provider = BuildProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        await service.CreateRoleAsync("tenant-reader", ["reports.read"]);
        await service.AssignRoleAsync("user-3", "tenant-reader", "tenant-a");

        var tenantA = Principal("user-3", new Claim("tenant_id", "tenant-a"));
        var tenantB = Principal("user-3", new Claim("tenant_id", "tenant-b"));

        Assert.True((await authorization.AuthorizeAsync(tenantA, null, "reports.read")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(tenantB, null, "reports.read")).Succeeded);
    }

    [Fact]
    public async Task Default_admin_role_and_bootstrap_user_are_ready_to_use()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonAuthorization(o => o.BootstrapAdminUserIds.Add("admin-1"));

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();

        Assert.True(await service.HasPermissionAsync("admin-1", "authorization.manage"));
        Assert.Contains("admin", await service.GetUserRolesAsync("admin-1"));
    }

    [Fact]
    public async Task Missing_authentication_scheme_fails_fast_for_management_endpoints()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonAuthorization();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            foreach (var service in hosted)
                await service.StartAsync(CancellationToken.None);
        });

        Assert.Contains("认证方案", error.Message);
    }

    [Fact]
    public async Task Invalid_and_duplicate_roles_are_rejected()
    {
        await using var provider = BuildProvider();
        var service = provider.GetRequiredService<RoleAuthorizationService>();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRoleAsync("x"));
        await service.CreateRoleAsync("valid-role");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRoleAsync("VALID-ROLE"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AssignRoleAsync("user", "missing"));
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure?.Invoke(services);
        services.AddLedonAuthorization();

        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(string userId, params Claim[] claims) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), .. claims],
            authenticationType: "test"));
}
