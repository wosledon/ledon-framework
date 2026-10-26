using Ledon.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>模块迁移：EnsureCreated、幂等、顺序与失败隔离。</summary>
public class MigrationRunnerTests
{
    [Fact]
    public async Task EnsureCreated_migration_runs_once_and_reports_status()
    {
        var database = Path.Combine(Path.GetTempPath(), $"ledon-migration-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MigratedContext>(options => options.UseSqlite($"Data Source={database}"));
        services.AddLedonMigration<MigratedContext>("module", ensureCreated: true);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();

        var first = await runner.MigrateAllAsync();
        Assert.True(first.Success);
        Assert.Equal(1, first.AppliedCount);

        var second = await runner.MigrateAllAsync();
        Assert.True(second.Success);
        Assert.Equal(0, second.AppliedCount);

        var status = Assert.Single(await runner.GetStatusAsync());
        Assert.Equal("module", status.Name);
        Assert.True(status.DatabaseExists);
        Assert.Empty(status.PendingMigrations);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Runner_orders_migrations_and_isolates_failures()
    {
        var order = new List<string>();

        var services = new ServiceCollection();
        services.AddSingleton(order);
        services.AddLedonMigration<SecondMigration>();
        services.AddLedonMigration<FirstMigration>();
        services.AddLedonMigration<BrokenMigration>();

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<MigrationRunner>();

        var report = await runner.MigrateAllAsync();

        Assert.False(report.Success);
        Assert.Equal(["first", "second"], order);
        Assert.Equal(2, report.Results.Count(result => result.Success && result.AppliedCount == 1));
        Assert.Single(report.Results, result => !result.Success && result.Name == "broken");
    }

    [Fact]
    public async Task Unknown_migration_name_throws()
    {
        var services = new ServiceCollection();
        services.AddLedonMigrations();

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<MigrationRunner>();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => runner.MigrateAsync("missing"));
    }

    private sealed class FirstMigration(List<string> order) : IModuleMigration
    {
        public string Name => "first";
        public int Order => 1;

        public Task<ModuleMigrationStatus> GetStatusAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleMigrationStatus(Name, true, [], []));

        public Task<int> ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            order.Add(Name);
            return Task.FromResult(1);
        }
    }

    private sealed class SecondMigration(List<string> order) : IModuleMigration
    {
        public string Name => "second";
        public int Order => 2;

        public Task<ModuleMigrationStatus> GetStatusAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleMigrationStatus(Name, true, [], []));

        public Task<int> ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            order.Add(Name);
            return Task.FromResult(1);
        }
    }

    private sealed class BrokenMigration : IModuleMigration
    {
        public string Name => "broken";
        public int Order => 3;

        public Task<ModuleMigrationStatus> GetStatusAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleMigrationStatus(Name, false, [], []));

        public Task<int> ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("迁移失败");
    }
}

public sealed class MigratedContext(DbContextOptions<MigratedContext> options) : DbContext(options)
{
    public DbSet<MigratedRow> Rows => Set<MigratedRow>();
}

public sealed class MigratedRow
{
    public int Id { get; set; }
}
