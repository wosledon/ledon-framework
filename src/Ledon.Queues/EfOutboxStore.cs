using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ledon.Queues;

/// <summary>EF Core 发件箱存储。</summary>
internal sealed class EfOutboxStore(OutboxDbContext context, LedonOutboxOptions options) : IOutboxStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public void Add(IIntegrationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            // 程序集限定名：Type.GetType 仅凭 FullName 只能解析调用方/mscorlib 程序集里的类型
            Type = @event.GetType().AssemblyQualifiedName ?? @event.GetType().FullName!,
            Payload = JsonSerializer.Serialize(@event, @event.GetType(), SerializerOptions),
            CreatedAt = DateTime.UtcNow,
        });
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize, string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(lease);

        // 候选：未处理、未超重试、且没人认领（或租约已过期，或本来就是自己认领的——
        // 最后一条让持有者能续约，否则长任务会在租约中途被别的节点接手）
        var candidates = await context.OutboxMessages
            .AsNoTracking()
            .Where(message => message.ProcessedAt == null
                && message.RetryCount < options.MaxRetries
                && (message.LeasedUntil == null
                    || message.LeasedUntil < now
                    || message.LeasedBy == owner))
            .OrderBy(message => message.CreatedAt)
            .Take(batchSize)
            .Select(message => message.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Length == 0)
            return [];

        // 逐条条件认领：WHERE 里带上租约条件，所以只有真正抢到的行才会被算进来。
        // 并发部署下两个实例可能同时看上同一条，这里保证只有一个能改成功
        var owned = new List<Guid>(candidates.Length);

        foreach (var id in candidates)
        {
            var rows = await context.OutboxMessages
                .Where(message => message.Id == id
                    && (message.LeasedUntil == null
                        || message.LeasedUntil < now
                        || message.LeasedBy == owner))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.LeasedBy, owner)
                    .SetProperty(message => message.LeasedUntil, until),
                    cancellationToken)
                .ConfigureAwait(false);

            if (rows == 1)
                owned.Add(id);
        }

        if (owned.Count == 0)
            return [];

        return await context.OutboxMessages
            .AsNoTracking()
            .Where(message => owned.Contains(message.Id))
            .OrderBy(message => message.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // 条件更新：只认自己认领过的行（别人的租约内不去动）
        await context.OutboxMessages
            .Where(message => message.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.ProcessedAt, DateTime.UtcNow)
                .SetProperty(message => message.Error, (string?)null)
                .SetProperty(message => message.LeasedBy, (string?)null)
                .SetProperty(message => message.LeasedUntil, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default)
    {
        // 释放认领让下轮能重试；重试计数 +1
        await context.OutboxMessages
            .Where(message => message.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.RetryCount, message => message.RetryCount + 1)
                .SetProperty(message => message.Error, error)
                .SetProperty(message => message.LeasedBy, (string?)null)
                .SetProperty(message => message.LeasedUntil, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> DeleteProcessedAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
            return 0;

        var cutoff = DateTime.UtcNow - retention;
        return await context.OutboxMessages
            .Where(message => message.ProcessedAt != null && message.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int batchSize, CancellationToken cancellationToken = default) =>
        await context.OutboxMessages
            .Where(message => message.ProcessedAt == null && message.RetryCount >= options.MaxRetries)
            .OrderBy(message => message.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    public async Task<bool> ReviveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rows = await context.OutboxMessages
            .Where(message => message.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.RetryCount, 0)
                .SetProperty(message => message.Error, (string?)null)
                .SetProperty(message => message.LeasedBy, (string?)null)
                .SetProperty(message => message.LeasedUntil, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);

        return rows > 0;
    }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default) =>
        await context.OutboxMessages
            .Where(message => message.ProcessedAt == null && message.RetryCount < options.MaxRetries)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
}
