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

    /// <summary>
    /// 清理超过保留期的已处理消息，返回删除条数。
    /// <paramref name="retention"/> 为 <see cref="TimeSpan.Zero"/> 或负值时实现应直接返回 0。
    /// </summary>
    Task<int> DeleteProcessedAsync(TimeSpan retention, CancellationToken cancellationToken = default);

    /// <summary>取搁置（未处理且重试次数已达上限）的消息——死信，需人工介入。</summary>
    Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重放搁置消息：清零重试计数与错误，使其重新进入投递队列。
    /// 返回 false 表示消息不存在（已被清理或手工删除）。
    /// </summary>
    Task<bool> ReviveAsync(Guid id, CancellationToken cancellationToken = default);
}
