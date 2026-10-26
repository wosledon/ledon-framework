using Ledon.Data.MultiTenancy;
using Ledon.MultiTenancy;
using Ledon.MultiTenancy.Management;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>租户数据库路由：连接解析、按租户建库、迁移编排。</summary>
public class TenantDataTests
{
    [Fact]
    public async Task Resolver_uses_tenant_catalog_and_default_connection()
    {
        var store = new InMemoryTenantStore();
        await store.AddAsync(new TenantInfo { Id = "tenant-a", ConnectionString = "Data Source=a.db" });
        await store.AddAsync(new TenantInfo { Id = "tenant-b" });
        await store.AddAsync(new TenantInfo { Id = "inactive", ConnectionString = "Data Source=x.db", Active = false });

        var resolver = new TenantStoreConnectionResolver(
            store,
            new FakeTenantContext("tenant-a"),
            new TenantDataOptions { DefaultConnectionString = "Data Source=default.db" });

        Assert.Equal("Data Source=a.db", await resolver.ResolveAsync());
        Assert.Equal("Data Source=default.db", await resolver.ResolveAsync("tenant-b"));
        await Assert.ThrowsAsync<TenantInactiveException>(() => resolver.ResolveAsync("inactive"));
        await Assert.ThrowsAsync<TenantNotFoundException>(() => resolver.ResolveAsync("missing"));

        var strict = new TenantStoreConnectionResolver(store, new FakeTenantContext(null), new TenantDataOptions());
        await Assert.ThrowsAsync<TenantConnectionNotConfiguredException>(() => strict.ResolveAsync());
    }

    [Fact]
    public async Task Factory_creates_context_per_tenant_database()
    {
        var directory = CreateTempDirectory();
        await using var provider = BuildProvider(out var store);
        await SeedTenantsAsync(store, directory);

        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory<TenantRowContext>>();

        await using (var tenantA = await factory.CreateAsync("tenant-a"))
        {
            await tenantA.Database.EnsureCreatedAsync();
            tenantA.Rows.Add(new TenantRow { Name = "a" });
            await tenantA.SaveChangesAsync();
        }

        await using (var tenantB = await factory.CreateAsync("tenant-b"))
        {
            await tenantB.Database.EnsureCreatedAsync();
            Assert.Equal(0, await tenantB.Rows.CountAsync());
        }

        await using (var tenantA = await factory.CreateAsync("tenant-a"))
            Assert.Equal(1, await tenantA.Rows.CountAsync());

        Assert.True(File.Exists(Path.Combine(directory, "a.db")));
        Assert.True(File.Exists(Path.Combine(directory, "b.db")));
    }

    [Fact]
    public async Task Migration_orchestrator_creates_all_active_tenant_databases()
    {
        var directory = CreateTempDirectory();
        await using var provider = BuildProvider(out var store);
        await SeedTenantsAsync(store, directory);

        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ITenantMigrationOrchestrator<TenantRowContext>>();

        var first = await orchestrator.MigrateAllAsync();
        Assert.Equal(2, first.Count(result => result.Succeeded));
        Assert.All(first, result => Assert.Equal(TenantMigrationOutcome.Created, result.Outcome));
        Assert.DoesNotContain(first, result => result.TenantId == "tenant-inactive");

        var second = await orchestrator.MigrateAllAsync();
        Assert.All(second, result => Assert.Equal(TenantMigrationOutcome.UpToDate, result.Outcome));
    }

    private static ServiceProvider BuildProvider(out InMemoryTenantStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonTenantData<TenantRowContext>(options =>
            options.ConfigureDbContext = (builder, connectionString) => builder.UseSqlite(connectionString));

        var provider = services.BuildServiceProvider();
        store = (InMemoryTenantStore)provider.GetRequiredService<ITenantStore>();

        return provider;
    }

    private static async Task SeedTenantsAsync(InMemoryTenantStore store, string directory)
    {
        await store.AddAsync(new TenantInfo
        {
            Id = "tenant-a",
            ConnectionString = $"Data Source={Path.Combine(directory, "a.db")}",
        });
        await store.AddAsync(new TenantInfo
        {
            Id = "tenant-b",
            ConnectionString = $"Data Source={Path.Combine(directory, "b.db")}",
        });
        await store.AddAsync(new TenantInfo
        {
            Id = "tenant-inactive",
            ConnectionString = $"Data Source={Path.Combine(directory, "inactive.db")}",
            Active = false,
        });
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ledon-tenant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;
    }
}

public sealed class TenantRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class TenantRowContext(DbContextOptions<TenantRowContext> options) : DbContext(options)
{
    public DbSet<TenantRow> Rows => Set<TenantRow>();
}
