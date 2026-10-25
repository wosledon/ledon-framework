using Ledon.Module.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Data;

/// <summary>
/// 数据库前置初始化：作为 <see cref="ILedonPreInitialization"/> 先于所有模块执行。
/// 策略见 <see cref="LedonMigrationStrategy"/>：默认自动（有挂起迁移则 Migrate，否则 EnsureCreated）。
/// </summary>
internal sealed class DbInitializer : ILedonPreInitialization
{
    private readonly IServiceProvider _services;
    private readonly LedonDataOptions _options;
    private readonly ILogger? _logger;

    public DbInitializer(IServiceProvider services, LedonDataOptions options)
    {
        _services = services;
        _options = options;
        // 日志可选：宿主没接日志时静默跳过（与模块系统一致）
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("Ledon.Data");
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.AutoMigrate || _options.MigrationStrategy == LedonMigrationStrategy.None)
            return;

        await using var scope = _services.CreateAsyncScope();

        foreach (var context in scope.ServiceProvider.GetServices<ILedonDbContext>())
        {
            if (context is not DbContext db)
                continue;

            await DbStructureInitializer.ApplyAsync(db, _options.MigrationStrategy, cancellationToken).ConfigureAwait(false);

            _logger?.LogDebug("数据库结构就绪（{Context}，策略 {Strategy}）", db.GetType().Name, _options.MigrationStrategy);
        }
    }
}

/// <summary>按策略把库结构准备好（抽出来便于单测与复用）。</summary>
internal static class DbStructureInitializer
{
    public static async Task ApplyAsync(
        DbContext db,
        LedonMigrationStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        switch (strategy)
        {
            case LedonMigrationStrategy.None:
                // 结构由部署流水线/DBA 管理，启动不碰库
                return;

            case LedonMigrationStrategy.MigrateOnly:
                var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
                if (pending.Any())
                    await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                break;

            case LedonMigrationStrategy.EnsureCreatedOnly:
                await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                var auto = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
                if (auto.Any())
                    await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                else
                    await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}

/// <summary>迁移脚本生成（部署流水线用）：把迁移变成可评审、可重复执行的 SQL 文件。</summary>
public static class LedonMigrationScriptExtensions
{
    /// <summary>
    /// 生成从 <paramref name="from"/> 到 <paramref name="to"/> 的 SQL 脚本
    /// （<paramref name="from"/> 默认 <c>0</c> = 从空库开始，<paramref name="to"/> 默认最新）。
    /// 幂等性由提供程序决定：SQLite 不支持幂等脚本生成（会抛
    /// <see cref="NotSupportedException"/>），SQL Server / PostgreSQL 支持
    /// （传 <see cref="MigrationsSqlGenerationOptions.Idempotent"/>）。
    /// </summary>
    public static string GenerateMigrationScript(
        this DbContext context,
        string from = "0",
        string? to = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var migrator = context.GetService<IMigrator>();

        return migrator.GenerateScript(from, to, options);
    }

    /// <summary>生成脚本并写入文件（返回文件路径）。</summary>
    public static async Task<string> WriteMigrationScriptAsync(
        this DbContext context,
        string outputPath,
        string from = "0",
        string? to = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default,
        CancellationToken cancellationToken = default)
    {
        var script = context.GenerateMigrationScript(from, to, options);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(outputPath, script, cancellationToken).ConfigureAwait(false);

        return outputPath;
    }
}
