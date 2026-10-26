using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Security.Authorization;

/// <summary>/auth/authorization/* 角色与授权管理端点（默认不映射）。</summary>
public sealed class AuthorizationEndpoints(
    RoleAuthorizationService service,
    LedonAuthorizationOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/auth/authorization");

        if (options.RequireAuthorization)
            group.RequireAuthorization();

        group.MapGet("/roles", async (CancellationToken ct) =>
            Results.Ok(await service.ListRolesAsync(ct)));

        group.MapPost("/roles", async (CreateRoleRequest request, CancellationToken ct) =>
        {
            try
            {
                var role = await service.CreateRoleAsync(request.Name, request.Permissions, ct);
                return Results.Created($"/auth/authorization/roles/{role.Name}", role);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPut("/roles/{name}/permissions", async (string name, SetPermissionsRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.SetPermissionsAsync(name, request.Permissions, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/roles/{name}", async (string name, CancellationToken ct) =>
            await service.DeleteRoleAsync(name, ct) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/users/{userId}/roles", async (string userId, string? tenantId, CancellationToken ct) =>
            Results.Ok(await service.GetUserRolesAsync(userId, tenantId, ct)));

        group.MapPost("/users/{userId}/roles", async (string userId, AssignRoleRequest request, CancellationToken ct) =>
        {
            try
            {
                await service.AssignRoleAsync(userId, request.Role, request.TenantId, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/users/{userId}/roles/{role}", async (string userId, string role, string? tenantId, CancellationToken ct) =>
            await service.RemoveRoleAsync(userId, role, tenantId, ct) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/users/{userId}/permissions", async (string userId, string? tenantId, CancellationToken ct) =>
            Results.Ok(await service.GetUserPermissionsAsync(userId, tenantId, ct)));
    }
}

public sealed record CreateRoleRequest(string Name, IReadOnlyList<string>? Permissions = null);

public sealed record SetPermissionsRequest(IReadOnlyList<string> Permissions);

public sealed record AssignRoleRequest(string Role, string? TenantId = null);

/// <summary>RBAC 注册。</summary>
public static class LedonAuthorizationServiceCollectionExtensions
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddLedonAuthorization(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        Action<LedonAuthorizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonAuthorizationOptions();
        configure?.Invoke(options);

        if (options.CacheDuration < TimeSpan.Zero)
            throw new InvalidOperationException("LedonAuthorizationOptions.CacheDuration 不能为负数");

        services.AddAuthorization();
        services.AddMemoryCache();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRoleStore, InMemoryRoleStore>();
        services.TryAddSingleton<IPermissionEvaluator, CachingPermissionEvaluator>();
        services.TryAddSingleton<RoleAuthorizationService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, RolePermissionAuthorizationHandler>());

        // Ledon.Security 已注册动态权限策略提供器时保留它；否则提供同名能力。
        var provider = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == typeof(Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider));

        if (provider is null || provider.ImplementationType == typeof(Microsoft.AspNetCore.Authorization.DefaultAuthorizationPolicyProvider))
        {
            services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
            services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, RolePermissionPolicyProvider>();
        }

        if (options.MapEndpoints)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, AuthorizationEndpoints>());

        return services;
    }
}
