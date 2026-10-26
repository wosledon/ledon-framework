using Ledon.MultiTenancy.Management;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 租户管理入口：
/// <code>
/// builder.Services.AddLedonTenantManagement(o =&gt; o.RequireAuthorization = true);
/// </code>
/// 提供租户目录 CRUD 与开通/回收钩子；端点随 <c>UseLedonWeb()</c> 自动映射。
/// </summary>
public static class LedonTenantManagementServiceCollectionExtensions
{
    public static IServiceCollection AddLedonTenantManagement(
        this IServiceCollection services,
        Action<LedonTenantManagementOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonTenantManagementOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ITenantStore, InMemoryTenantStore>();
        services.TryAddScoped<TenantManagementService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, TenantManagementEndpoints>());

        return services;
    }
}

/// <summary>租户管理选项。</summary>
public sealed class LedonTenantManagementOptions
{
    /// <summary>管理端点是否要求授权，默认 <c>false</c>（生产建议开启）。</summary>
    public bool RequireAuthorization { get; set; }
}

/// <summary>租户管理端点。</summary>
public sealed class TenantManagementEndpoints(
    IServiceScopeFactory scopeFactory,
    LedonTenantManagementOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/tenants");

        if (options.RequireAuthorization)
            group.RequireAuthorization();

        group.MapGet("/", async (CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantManagementService>();

            return Results.Ok(await service.ListAsync(ct));
        });

        group.MapGet("/{id}", async (string id, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantManagementService>();
            var tenant = await service.FindAsync(id, ct);

            return tenant is null ? Results.NotFound() : Results.Ok(tenant);
        });

        group.MapPost("/", async (CreateTenantRequest request, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantManagementService>();

            try
            {
                var tenant = await service.CreateAsync(request.Id, request.Name, request.ConnectionString, ct);
                return Results.Created($"/tenants/{tenant.Id}", tenant);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("已存在", StringComparison.Ordinal)
                    ? Results.Conflict(new { error = ex.Message })
                    : Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPut("/{id}", async (string id, UpdateTenantRequest request, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantManagementService>();

            try
            {
                var tenant = await service.UpdateAsync(id, request.Name, request.ConnectionString, request.Active, ct);
                return Results.Ok(tenant);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapDelete("/{id}", async (string id, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantManagementService>();

            return await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });
    }
}

/// <summary>创建租户请求。</summary>
public sealed record CreateTenantRequest(
    string Id,
    string? Name = null,
    string? ConnectionString = null);

/// <summary>更新租户请求。</summary>
public sealed record UpdateTenantRequest(
    string? Name,
    string? ConnectionString,
    bool Active = true);
