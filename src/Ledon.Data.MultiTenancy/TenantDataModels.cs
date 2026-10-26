using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ledon.MultiTenancy.Management;

namespace Ledon.Data.MultiTenancy;

/// <summary>按租户路由数据库连接。</summary>
public sealed class TenantDataOptions
{
    /// <summary>
    /// 共享/默认数据库连接串：租户未配置专属连接串时回退到这里（单库 + TenantId 过滤模式）。
    /// </summary>
    public string? DefaultConnectionString { get; set; }

    /// <summary>
    /// 配置 DbContext 的提供程序，例如：
    /// <code>
    /// o.ConfigureDbContext = (builder, connectionString) =&gt; builder.UseNpgsql(connectionString);
    /// </code>
    /// </summary>
    public Action<DbContextOptionsBuilder, string>? ConfigureDbContext { get; set; }

    /// <summary>迁移策略，默认跟随 <see cref="Ledon.Data.LedonMigrationStrategy.Auto"/>。</summary>
    public LedonMigrationStrategy MigrationStrategy { get; set; } = LedonMigrationStrategy.Auto;

    /// <summary>是否迁移共享/默认数据库（配置了 <see cref="DefaultConnectionString"/> 时），默认 <c>true</c>。</summary>
    public bool MigrateDefaultDatabase { get; set; } = true;

    /// <summary>是否包含已停用租户，默认 <c>false</c>。</summary>
    public bool IncludeInactiveTenants { get; set; }
}

/// <summary>解析租户数据库连接串。</summary>
public interface ITenantConnectionResolver
{
    /// <summary>解析指定租户；<paramref name="tenantId"/> 为空时取当前 <c>ITenantContext</c>。</summary>
    Task<string> ResolveAsync(string? tenantId = null, CancellationToken cancellationToken = default);
}

/// <summary>租户不存在。</summary>
public sealed class TenantNotFoundException(string tenantId)
    : InvalidOperationException($"租户不存在：{tenantId}");

/// <summary>租户已停用。</summary>
public sealed class TenantInactiveException(string tenantId)
    : InvalidOperationException($"租户已停用：{tenantId}");

/// <summary>租户未配置数据库连接串，且没有默认连接串。</summary>
public sealed class TenantConnectionNotConfiguredException(string tenantId)
    : InvalidOperationException($"租户 {tenantId} 未配置数据库连接串，且未设置 DefaultConnectionString");

/// <summary>基于 <c>ITenantStore</c> 的租户连接解析（租户目录可替换为数据库实现）。</summary>
public sealed class TenantStoreConnectionResolver(
    ITenantStore store,
    Ledon.MultiTenancy.ITenantContext tenantContext,
    TenantDataOptions options) : ITenantConnectionResolver
{
    public async Task<string> ResolveAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        tenantId ??= tenantContext.TenantId;

        if (string.IsNullOrWhiteSpace(tenantId))
            return options.DefaultConnectionString
                ?? throw new TenantConnectionNotConfiguredException("<none>");

        var tenant = await store.FindAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantNotFoundException(tenantId);

        if (!tenant.Active)
            throw new TenantInactiveException(tenantId);

        if (!string.IsNullOrWhiteSpace(tenant.ConnectionString))
            return tenant.ConnectionString;

        return options.DefaultConnectionString
            ?? throw new TenantConnectionNotConfiguredException(tenantId);
    }
}

/// <summary>按租户创建 DbContext（不注册到 DI 容器，调用方负责释放）。</summary>
public interface ITenantDbContextFactory<TContext> where TContext : DbContext
{
    Task<TContext> CreateAsync(string? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>用显式连接串创建（迁移共享库或指定库时用）。</summary>
    Task<TContext> CreateWithConnectionAsync(string connectionString, CancellationToken cancellationToken = default);
}

internal sealed class TenantDbContextFactory<TContext>(
    IServiceProvider services,
    ITenantConnectionResolver resolver,
    TenantDataOptions options) : ITenantDbContextFactory<TContext> where TContext : DbContext
{
    public async Task<TContext> CreateAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var connectionString = await resolver.ResolveAsync(tenantId, cancellationToken).ConfigureAwait(false);

        return CreateCore(connectionString);
    }

    public Task<TContext> CreateWithConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return Task.FromResult(CreateCore(connectionString));
    }

    private TContext CreateCore(string connectionString)
    {
        if (options.ConfigureDbContext is null)
        {
            throw new InvalidOperationException(
                "TenantDataOptions.ConfigureDbContext 未配置：请在 AddLedonTenantData 中指定 UseSqlite/UseNpgsql/UseSqlServer 等提供程序。");
        }

        var builder = new DbContextOptionsBuilder<TContext>();
        options.ConfigureDbContext(builder, connectionString);

        return ActivatorUtilities.CreateInstance<TContext>(services, builder.Options);
    }
}

/// <summary>单个租户的迁移结果。</summary>
public enum TenantMigrationOutcome
{
    UpToDate,
    Created,
    Migrated,
    Skipped,
    Failed,
}

/// <summary>单个租户的迁移状态。</summary>
public sealed record TenantMigrationResult(
    string TenantId,
    TenantMigrationOutcome Outcome,
    int PendingMigrations = 0,
    string? Error = null)
{
    public bool Succeeded => Outcome != TenantMigrationOutcome.Failed;
}

/// <summary>租户迁移编排：按租户建库/迁移并汇总结果。</summary>
public interface ITenantMigrationOrchestrator<TContext> where TContext : DbContext
{
    Task<TenantMigrationResult> MigrateAsync(string tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantMigrationResult>> MigrateAllAsync(CancellationToken cancellationToken = default);
}

internal sealed class TenantMigrationOrchestrator<TContext>(
    ITenantStore store,
    ITenantDbContextFactory<TContext> factory,
    TenantDataOptions options) : ITenantMigrationOrchestrator<TContext> where TContext : DbContext
{
    public async Task<TenantMigrationResult> MigrateAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await MigrateTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TenantMigrationResult>> MigrateAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<TenantMigrationResult>();

        if (options.MigrateDefaultDatabase && !string.IsNullOrWhiteSpace(options.DefaultConnectionString))
            results.Add(await MigrateTenantAsync(
                "<default>",
                cancellationToken,
                options.DefaultConnectionString).ConfigureAwait(false));

        var tenants = await store.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var tenant in tenants)
        {
            if (!tenant.Active && !options.IncludeInactiveTenants)
                continue;

            results.Add(await MigrateTenantAsync(tenant.Id, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<TenantMigrationResult> MigrateTenantAsync(
        string tenantId,
        CancellationToken cancellationToken,
        string? connectionString = null)
    {
        try
        {
            await using var db = connectionString is null
                ? await factory.CreateAsync(tenantId, cancellationToken).ConfigureAwait(false)
                : await factory.CreateWithConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false);

            return options.MigrationStrategy switch
            {
                LedonMigrationStrategy.None => new TenantMigrationResult(tenantId, TenantMigrationOutcome.Skipped),

                LedonMigrationStrategy.EnsureCreatedOnly => await EnsureCreatedAsync(tenantId, db, cancellationToken)
                    .ConfigureAwait(false),

                LedonMigrationStrategy.MigrateOnly => await MigrateOnlyAsync(tenantId, db, cancellationToken)
                    .ConfigureAwait(false),

                _ => await AutoAsync(tenantId, db, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TenantMigrationResult(tenantId, TenantMigrationOutcome.Failed, Error: ex.Message);
        }
    }

    private static async Task<TenantMigrationResult> EnsureCreatedAsync(
        string tenantId,
        TContext db,
        CancellationToken cancellationToken)
    {
        var created = await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        return new TenantMigrationResult(
            tenantId,
            created ? TenantMigrationOutcome.Created : TenantMigrationOutcome.UpToDate);
    }

    private static async Task<TenantMigrationResult> MigrateOnlyAsync(
        string tenantId,
        TContext db,
        CancellationToken cancellationToken)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();

        if (pending.Length == 0)
            return new TenantMigrationResult(tenantId, TenantMigrationOutcome.UpToDate);

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        return new TenantMigrationResult(tenantId, TenantMigrationOutcome.Migrated, pending.Length);
    }

    private static async Task<TenantMigrationResult> AutoAsync(
        string tenantId,
        TContext db,
        CancellationToken cancellationToken)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();

        if (pending.Length > 0)
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            return new TenantMigrationResult(tenantId, TenantMigrationOutcome.Migrated, pending.Length);
        }

        return await EnsureCreatedAsync(tenantId, db, cancellationToken).ConfigureAwait(false);
    }
}
