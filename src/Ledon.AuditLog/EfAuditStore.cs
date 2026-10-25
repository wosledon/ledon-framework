using Microsoft.EntityFrameworkCore;

namespace Ledon.AuditLog;

/// <summary>EF 审计存储：写入业务库的 <c>AuditEntries</c> 表（独立保存，不参与业务事务）。</summary>
public sealed class EfAuditStore<TContext>(IDbContextFactory<TContext> factory) : IAuditStore
    where TContext : DbContext
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.Add(entry);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(
        string? entityType = null, string? entityId = null, int count = 50, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Set<AuditEntry>()
            .AsNoTracking()
            .Where(entry => entityType == null || entry.EntityType == entityType)
            .Where(entry => entityId == null || entry.EntityId == entityId)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(Math.Max(1, count))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Set<AuditEntry>()
            .Where(entry => entry.TimestampUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
