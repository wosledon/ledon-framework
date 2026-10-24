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

    /// <summary>收件箱（消费侧去重）。注册 <see cref="IInboxStore"/> 后生效，表结构随基类一并约定。</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>持久化后台队列（AddLedonDurableQueue 后生效，表结构随基类一并约定）。</summary>
    public DbSet<QueuedJobMessage> QueuedJobs => Set<QueuedJobMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Type).IsRequired().HasMaxLength(500);
            entity.Property(message => message.Payload).IsRequired();
            entity.HasIndex(message => message.ProcessedAt);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);

            // MessageId 唯一：并发认领靠唯一约束决出胜者（Insert 冲突 = 已被认领）
            entity.HasIndex(message => message.MessageId).IsUnique();
            entity.Property(message => message.MessageId).IsRequired().HasMaxLength(200);
            entity.Property(message => message.EventType).IsRequired().HasMaxLength(500);
            entity.HasIndex(message => message.CompletedAt);
        });

        modelBuilder.Entity<QueuedJobMessage>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.QueueName).IsRequired().HasMaxLength(200);
            entity.Property(job => job.Payload).IsRequired();

            // 调度器按 未处理 + 可用时刻 取批次
            entity.HasIndex(job => new { job.ProcessedAt, job.AvailableAt });
        });

        base.OnModelCreating(modelBuilder);
    }
}
