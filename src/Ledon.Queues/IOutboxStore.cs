namespace Ledon.Queues;

/// <summary>发件箱存储抽象。替换实现即可换存储（默认 EF Core）。</summary>
public interface IOutboxStore
{
    /// <summary>把集成事件写入发件箱（与业务数据在同一 SaveChanges 事务里，调用方负责保存）。</summary>
    void Add(IIntegrationEvent @event);

    /// <summary>取一批待投递消息（未处理且未超重试上限）。</summary>
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>标记已投递。</summary>
    Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>标记投递失败（重试计数 +1）。</summary>
    Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default);
}
