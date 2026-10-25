using Microsoft.EntityFrameworkCore;

namespace Ledon.AuditLog;

/// <summary>
/// 审计流水上下文：业务上下文继承它（或自行含 <c>DbSet&lt;AuditEntry&gt;</c>）即可把
/// 审计写入同一数据库、同一事务外的独立保存。
/// </summary>
public class AuditLogDbContext(DbContextOptions<AuditLogDbContext> options) : DbContext(options)
{
    /// <summary>审计流水。</summary>
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.EntityType).HasMaxLength(200);
            entity.Property(entry => entry.EntityId).HasMaxLength(200);
            entity.Property(entry => entry.Changes).HasMaxLength(8000);
            entity.HasIndex(entry => new { entry.EntityType, entry.EntityId });
            entity.HasIndex(entry => entry.TimestampUtc);
        });

        base.OnModelCreating(modelBuilder);
    }
}
