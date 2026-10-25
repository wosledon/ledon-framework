namespace Ledon.Queues;

/// <summary>发件箱存储抽象。替换实现即可换存储（默认 EF Core）。</summary>
public interface IOutboxStore
{
    /// <summary>把集成事件写入发件箱（与业务数据在同一 SaveChanges 事务里，调用方负责保存）。</summary>
    void Add(IIntegrationEvent @event);

    /// <summary>
    /// 取一批待投递消息**并原子认领**：只有第一个认领的实例能拿到
    /// （多实例部署时同一条消息不会被每个实例各投一遍）。
    /// </summary>
    /// <param name="batchSize">单轮最多认领多少条。</param>
    /// <param name="owner">认领者标识（实例唯一）。</param>
    /// <param name="lease">租约时长；到期未处理完别的实例可接手（节点崩溃兜底）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>标记已投递（同时释放认领）。</summary>
    Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>标记投递失败（重试计数 +1，同时释放认领以便下轮重试）。</summary>
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

    /// <summary>
    /// 统计待投递条数（未处理且未超重试上限，含已被认领的）。
    /// 供运维快照使用——不认领，所以不会把别人正在投的消息抢过来。
    /// </summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken = default);
}
