using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Data.Migrations;

/// <summary>单个模块的迁移状态。</summary>
public sealed record ModuleMigrationStatus(
    string Name,
    bool DatabaseExists,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations,
    string? Error = null);

/// <summary>单个模块的迁移结果。</summary>
public sealed record ModuleMigrationResult(string Name, bool Success, int AppliedCount, string? Error = null);

/// <summary>迁移报告。</summary>
public sealed record ModuleMigrationReport(IReadOnlyList<ModuleMigrationResult> Results)
{
    public bool Success => Results.All(result => result.Success);

    public int AppliedCount => Results.Sum(result => result.AppliedCount);
}

/// <summary>模块迁移：状态查询与执行。</summary>
public interface IModuleMigration
{
    string Name { get; }

    /// <summary>执行顺序（小的先执行），默认 0。</summary>
    int Order => 0;

    Task<ModuleMigrationStatus> GetStatusAsync(IServiceProvider services, CancellationToken cancellationToken = default);

    /// <summary>执行迁移并返回本次执行的操作数。</summary>
    Task<int> ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default);
}

/// <summary>基于 EF Core 的模块迁移（支持 Migrate 与 EnsureCreated）。</summary>
public sealed class EfModuleMigration<TContext>(string name, bool ensureCreated = false) : IModuleMigration
    where TContext : DbContext
{
    public string Name { get; } = name;

    public int Order { get; init; }

    public async Task<ModuleMigrationStatus> GetStatusAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<TContext>();

        try
        {
            var exists = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);

            if (!exists)
                return new ModuleMigrationStatus(Name, false, [], []);

            // EnsureCreated 不创建 __EFMigrationsHistory，查询迁移历史会报错
            if (ensureCreated)
                return new ModuleMigrationStatus(Name, true, [], []);

            var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();

            return new ModuleMigrationStatus(Name, true, applied, pending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModuleMigrationStatus(Name, false, [], [], ex.Message);
        }
    }

    public async Task<int> ApplyAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<TContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();

        if (pending.Length > 0)
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            return pending.Length;
        }

        if (!ensureCreated)
            return 0;

        return await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false) ? 1 : 0;
    }
}

/// <summary>模块迁移编排。</summary>
public sealed class MigrationRunner(
    IEnumerable<IModuleMigration> migrations,
    IServiceScopeFactory scopeFactory)
{
    public IReadOnlyList<IModuleMigration> Migrations { get; } =
        migrations.OrderBy(migration => migration.Order).ThenBy(migration => migration.Name, StringComparer.Ordinal).ToArray();

    public async Task<IReadOnlyList<ModuleMigrationStatus>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<ModuleMigrationStatus>(Migrations.Count);

        foreach (var migration in Migrations)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            results.Add(await migration.GetStatusAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public Task<ModuleMigrationReport> MigrateAllAsync(CancellationToken cancellationToken = default) =>
        MigrateAsync(Migrations, cancellationToken);

    public async Task<ModuleMigrationReport> MigrateAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var migration = Migrations.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"模块迁移不存在：{name}");

        return await MigrateAsync([migration], cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModuleMigrationReport> MigrateAsync(
        IEnumerable<IModuleMigration> selected,
        CancellationToken cancellationToken)
    {
        var results = new List<ModuleMigrationResult>();

        foreach (var migration in selected)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var applied = await migration.ApplyAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                results.Add(new ModuleMigrationResult(migration.Name, true, applied));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new ModuleMigrationResult(migration.Name, false, 0, ex.Message));
            }
        }

        return new ModuleMigrationReport(results);
    }
}

/// <summary>模块迁移注册。</summary>
public static class LedonMigrationServiceCollectionExtensions
{
    public static IServiceCollection AddLedonMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<MigrationRunner>();

        return services;
    }

    /// <summary>
    /// 注册一个 EF Core 模块迁移：
    /// <code>
    /// services.AddLedonMigration&lt;AppDbContext&gt;("app");
    /// </code>
    /// </summary>
    public static IServiceCollection AddLedonMigration<TContext>(
        this IServiceCollection services,
        string name,
        bool ensureCreated = false,
        int order = 0)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.AddLedonMigrations();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigration>(
            new EfModuleMigration<TContext>(name, ensureCreated) { Order = order }));

        return services;
    }

    /// <summary>注册自定义迁移实现。</summary>
    public static IServiceCollection AddLedonMigration<TMigration>(this IServiceCollection services)
        where TMigration : class, IModuleMigration
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLedonMigrations();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigration, TMigration>());

        return services;
    }
}
