using Ledon.Data.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>迁移编排边界：空集合与只执行指定模块。</summary>
public class MigrationRunnerEdgeTests
{
    [Fact]
    public async Task Empty_runner_succeeds_without_operations()
    {
        var services = new ServiceCollection();
        services.AddLedonMigrations();

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<MigrationRunner>();
        var report = await runner.MigrateAllAsync();

        Assert.True(report.Success);
        Assert.Empty(report.Results);
        Assert.Equal(0, report.AppliedCount);
    }

    [Fact]
    public async Task Named_migration_runs_only_selected_module()
    {
        MigrationLog.Clear();
        var services = new ServiceCollection();
        services.AddLedonMigration<EdgeMigrationOne>();
        services.AddLedonMigration<EdgeMigrationTwo>();

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<MigrationRunner>();
        var report = await runner.MigrateAsync("edge-one");

        Assert.True(report.Success);
        Assert.Single(report.Results);
        Assert.Equal(["edge-one"], MigrationLog.Items);
    }

    private static class MigrationLog
    {
        private static readonly List<string> Entries = [];

        public static IReadOnlyList<string> Items
        {
            get
            {
                lock (Entries)
                    return Entries.ToArray();
            }
        }

        public static void Add(string name)
        {
            lock (Entries)
                Entries.Add(name);
        }

        public static void Clear()
        {
            lock (Entries)
                Entries.Clear();
        }
    }

    public sealed class EdgeMigrationOne : IModuleMigration
    {
        public string Name => "edge-one";
        public int Order => 1;

        public Task<ModuleMigrationStatus> GetStatusAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleMigrationStatus(Name, true, [], []));

        public Task<int> ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            MigrationLog.Add(Name);
            return Task.FromResult(1);
        }
    }

    public sealed class EdgeMigrationTwo : IModuleMigration
    {
        public string Name => "edge-two";
        public int Order => 2;

        public Task<ModuleMigrationStatus> GetStatusAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleMigrationStatus(Name, true, [], []));

        public Task<int> ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            MigrationLog.Add(Name);
            return Task.FromResult(1);
        }
    }
}
