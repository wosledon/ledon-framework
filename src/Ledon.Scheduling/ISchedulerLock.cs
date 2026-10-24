namespace Ledon.Scheduling;

/// <summary>
/// 调度领导权锁（多实例部署）。只有持有锁的实例执行定时任务；
/// 锁带 TTL 并周期性续约，掉锁（续约失败/网络分区）时实例自动让出并重新竞选。
/// 单实例部署无需实现本接口（不注册即为不选举）。
/// </summary>
public interface ISchedulerLock : IAsyncDisposable
{
    /// <summary>尝试获取领导权；成功返回 true。</summary>
    Task<bool> AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>续约（延长 TTL）；失败（锁已被其它实例拿走）返回 false。</summary>
    Task<bool> RenewAsync(CancellationToken cancellationToken = default);

    /// <summary>主动释放领导权。</summary>
    Task ReleaseAsync(CancellationToken cancellationToken = default);
}
