using Microsoft.EntityFrameworkCore;

namespace Ledon.Queues;

/// <summary>EF Core 收件箱存储（与发件箱同库，复用 <see cref="OutboxDbContext"/>）。</summary>
internal sealed class EfInboxStore(OutboxDbContext context, LedonInboxOptions options) : IInboxStore
{
    public async Task<bool> TryClaimAsync(string messageId, string eventType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        var now = DateTime.UtcNow;
        var existing = await context.InboxMessages
            .FirstOrDefaultAsync(message => message.MessageId == messageId, cancellationToken);

        if (existing is null)
        {
            context.InboxMessages.Add(new InboxMessage
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                EventType = eventType,
                ClaimedAt = now,
                ClaimCount = 1,
            });

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                // 唯一约束冲突：并发场景下已被其他实例认领（去重生效）
                return false;
            }
        }

        if (existing.CompletedAt is not null)
            return false;   // 已处理完成：重复投递

        if (now - existing.ClaimedAt < options.ClaimTimeout)
            return false;   // 正在被处理（认领未超时）

        // 陈旧认领（上次处理进程崩溃）：重置认领重试，至少一次语义不被破坏
        existing.ClaimedAt = now;
        existing.ClaimCount++;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkCompletedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var message = await context.InboxMessages
            .FirstOrDefaultAsync(m => m.MessageId == messageId, cancellationToken);

        if (message is not null)
        {
            message.CompletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> DeleteCompletedAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
            return 0;

        var cutoff = DateTime.UtcNow - retention;
        return await context.InboxMessages
            .Where(message => message.CompletedAt != null && message.CompletedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
