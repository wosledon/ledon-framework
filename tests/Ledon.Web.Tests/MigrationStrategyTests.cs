using Ledon.Data;
using Ledon.Module.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ledon.Web.Tests;

/// <summary>启动迁移策略：None/MigrateOnly 不建表、EnsureCreatedOnly/Auto 建表、脚本生成。</summary>
public class MigrationStrategyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ledon-mig-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(LedonMigrationStrategy.None)]
    [InlineData(LedonMigrationStrategy.MigrateOnly)]
    public async Task Strategy_without_migrations_creates_no_tables(LedonMigrationStrategy strategy)
    {
        await using var context = CreateContext();

        await DbStructureInitializer.ApplyAsync(context, strategy);

        // 没有挂起迁移：MigrateOnly 什么都不做；None 更不该碰库
        Assert.False(await TableExistsAsync(context, "Notes"));
    }

    [Fact]
    public async Task Ensure_created_only_builds_schema_from_model()
    {
        await using var context = CreateContext();

        await DbStructureInitializer.ApplyAsync(context, LedonMigrationStrategy.EnsureCreatedOnly);

        Assert.True(await TableExistsAsync(context, "Notes"));
    }

    [Fact]
    public async Task Auto_strategy_falls_back_to_ensure_created()
    {
        await using var context = CreateContext();

        await DbStructureInitializer.ApplyAsync(context, LedonMigrationStrategy.Auto);

        Assert.True(await TableExistsAsync(context, "Notes"));
    }

    [Fact]
    public async Task Migration_script_can_be_generated_and_written()
    {
        await using var context = CreateContext();

        var path = Path.Combine(Path.GetTempPath(), $"ledon-script-{Guid.NewGuid():N}.sql");

        try
        {
            var written = await LedonMigrationScriptExtensions.WriteMigrationScriptAsync(context, path);

            Assert.Equal(path, written);
            Assert.True(File.Exists(written));

            var sql = await File.ReadAllTextAsync(written);
            Assert.Contains("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private MigrationsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MigrationsDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options);

    private static async Task<bool> TableExistsAsync(DbContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = table;
            command.Parameters.Add(parameter);

            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.SuppressFinalize(this);

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [DisableAutoDiscovery]   // 测试夹具：只在本测试内手动构造，不进自动发现
    private sealed class MigrationsDbContext(DbContextOptions<MigrationsDbContext> options) : DbContext(options), ILedonDbContext
    {
        public DbSet<MigrationNote> Notes => Set<MigrationNote>();
    }

    private sealed class MigrationNote
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }
}
