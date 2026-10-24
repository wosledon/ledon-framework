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

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
        await context.OutboxMessages
            .Where(message => message.ProcessedAt == null && message.RetryCount < options.MaxRetries)
            .OrderBy(message => message.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    public async Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var message = await context.OutboxMessages.FindAsync([id], cancellationToken);
        if (message is not null)
        {
            message.ProcessedAt = DateTime.UtcNow;
            message.Error = null;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default)
    {
        var message = await context.OutboxMessages.FindAsync([id], cancellationToken);
        if (message is not null)
        {
            message.RetryCount++;
            message.Error = error;
            await context.SaveChangesAsync(cancellationToken);
        }
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
        var message = await context.OutboxMessages.FindAsync([id], cancellationToken);
        if (message is null)
            return false;

        message.RetryCount = 0;
        message.Error = null;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
