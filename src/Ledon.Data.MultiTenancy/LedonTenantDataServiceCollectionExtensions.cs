using Ledon.Data;
using Ledon.Data.MultiTenancy;
using Ledon.MultiTenancy.Management;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 租户数据库路由入口：
/// <code>
/// builder.Services.AddLedonTenantData&lt;AppDbContext&gt;(o =&gt;
/// {
///     o.DefaultConnectionString = configuration.GetConnectionString("Default");
///     o.ConfigureDbContext = (builder, connectionString) =&gt; builder.UseNpgsql(connectionString);
/// });
/// </code>
/// 复用 <c>ITenantStore</c>（未注册时自动使用内存租户目录）与 <c>ITenantContext</c>。
/// </summary>
public static class LedonTenantDataServiceCollectionExtensions
{
    public static IServiceCollection AddLedonTenantData<TContext>(
        this IServiceCollection services,
        Action<TenantDataOptions>? configure = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TenantDataOptions();
        configure?.Invoke(options);

        services.AddLedonMultiTenancy();

        // 只注册租户目录，不注册管理端点：避免仅接入数据库路由时意外暴露 /tenants CRUD
        services.TryAddSingleton<ITenantStore, InMemoryTenantStore>();

        services.TryAddSingleton(options);
        services.TryAddSingleton<ITenantConnectionResolver, TenantStoreConnectionResolver>();
        services.TryAddScoped<ITenantDbContextFactory<TContext>, TenantDbContextFactory<TContext>>();
        services.TryAddScoped<ITenantMigrationOrchestrator<TContext>, TenantMigrationOrchestrator<TContext>>();

        return services;
    }
}
