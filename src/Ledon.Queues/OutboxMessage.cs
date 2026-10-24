using Microsoft.EntityFrameworkCore;

namespace Ledon.Queues;

/// <summary>发件箱消息：与业务数据同事务落库，由调度器异步投递（至少一次）。</summary>
public class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>事件类型全名（反序列化用）。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>事件 JSON 载荷。</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public int RetryCount { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// 带发件箱的 DbContext 基类：用户的 DbContext 继承它即获得
/// <see cref="OutboxMessages"/> 集合与表结构约定。
/// </summary>
public abstract class OutboxDbContext : DbContext
{
    protected OutboxDbContext()
    {
    }

    protected OutboxDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Type).IsRequired().HasMaxLength(500);
            entity.Property(message => message.Payload).IsRequired();
            entity.HasIndex(message => message.ProcessedAt);
        });

        base.OnModelCreating(modelBuilder);
    }
}
