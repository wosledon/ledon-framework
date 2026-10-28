using Ledon.Data;
using Ledon.Data.MultiTenancy;
using Ledon.MultiTenancy.Management;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>租户数据库边界：默认库迁移与 None 策略跳过。</summary>
public class TenantDataEdgeTests
{
    [Fact]
    public async Task Migrate_all_includes_default_database()
    {
        var directory = CreateDirectory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonTenantData<TenantRowContext>(options =>
        {
            options.DefaultConnectionString = $"Data Source={Path.Combine(directory, "default.db")}";
            options.ConfigureDbContext = (builder, connectionString) => builder.UseSqlite(connectionString);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ITenantStore>();
        await store.AddAsync(new TenantInfo
        {
            Id = "tenant-a",
            ConnectionString = $"Data Source={Path.Combine(directory, "tenant.db")}",
        });

        await using var scope = provider.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ITenantMigrationOrchestrator<TenantRowContext>>();
        var results = await orchestrator.MigrateAllAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.TenantId == "<default>" && result.Outcome == TenantMigrationOutcome.Created);
        Assert.Contains(results, result => result.TenantId == "tenant-a" && result.Outcome == TenantMigrationOutcome.Created);
    }

    [Fact]
    public async Task Migration_strategy_none_skips_database_operations()
    {
        var directory = CreateDirectory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonTenantData<TenantRowContext>(options =>
        {
            options.MigrationStrategy = LedonMigrationStrategy.None;
            options.ConfigureDbContext = (builder, connectionString) => builder.UseSqlite(connectionString);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ITenantStore>();
        await store.AddAsync(new TenantInfo
        {
            Id = "tenant-a",
            ConnectionString = $"Data Source={Path.Combine(directory, "tenant.db")}",
        });

        await using var scope = provider.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ITenantMigrationOrchestrator<TenantRowContext>>();
        var result = Assert.Single(await orchestrator.MigrateAllAsync());

        Assert.Equal(TenantMigrationOutcome.Skipped, result.Outcome);
        Assert.False(File.Exists(Path.Combine(directory, "tenant.db")));
    }

    [Fact]
    public async Task Orchestrator_rejects_invalid_tenant_id()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonTenantData<TenantRowContext>(options =>
            options.ConfigureDbContext = (builder, connectionString) => builder.UseSqlite(connectionString));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ITenantMigrationOrchestrator<TenantRowContext>>();

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.MigrateAsync("   "));
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ledon-tenant-edge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
