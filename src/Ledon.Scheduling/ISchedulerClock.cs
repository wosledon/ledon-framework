namespace Ledon.Scheduling;

/// <summary>
/// 调度器时钟抽象：把"现在几点 / 等待到某时刻"从真实时间里抽出来，
/// 让测试用虚拟时钟确定性地驱动 cron 触发（不必睡真实秒数）。
/// 生产实现 <see cref="SystemSchedulerClock"/> 由 <c>AddLedonScheduling</c> 注册。
/// </summary>
public interface ISchedulerClock
{
    /// <summary>当前时间（UTC）。</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>等待到 <paramref name="until"/>（真实实现 = Task.Delay；测试实现可立即完成）。</summary>
    Task DelayAsync(DateTimeOffset until, CancellationToken cancellationToken);
}

/// <summary>真实时钟（默认）。</summary>
internal sealed class SystemSchedulerClock : ISchedulerClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        var delay = until - UtcNow;
        return delay > TimeSpan.Zero
            ? Task.Delay(delay, cancellationToken)
            : Task.CompletedTask;
    }
}

/// <summary>
/// 测试用手动时钟：<see cref="AdvanceTo"/> 把当前时间推到目标时刻并放行等待者，
/// 使 cron 触发在测试中即时、确定地发生。
/// </summary>
public sealed class ManualSchedulerClock : ISchedulerClock
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _waiters = [];
    private DateTimeOffset _now;

    public ManualSchedulerClock(DateTimeOffset? startAt = null) => _now = startAt ?? DateTimeOffset.UnixEpoch;

    public DateTimeOffset UtcNow
    {
        get { lock (_gate) return _now; }
    }

    /// <summary>是否有等待中的唤醒点（测试用于确认调度循环已进入等待）。</summary>
    public bool HasWaiters
    {
        get { lock (_gate) return _waiters.Count > 0; }
    }

    public Task DelayAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (until <= _now)
                return Task.CompletedTask;

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
            return waiter.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>推进到目标时刻，放行所有等待者（取消的等待者顺带清理）。</summary>
    public void AdvanceTo(DateTimeOffset until)
    {
        TaskCompletionSource[] pending;
        lock (_gate)
        {
            if (until < _now)
                throw new ArgumentException("只能向前推进时间", nameof(until));

            _now = until;
            pending = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var waiter in pending)
            waiter.TrySetResult();
    }
}
