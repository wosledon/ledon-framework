namespace Ledon.Scheduling;

/// <summary>
/// 定时任务契约。实现类被自动发现；<see cref="CronExpression"/> 为 6 段（含秒）或 5 段 cron 表达式。
/// 同一任务实例是单例——执行中不会并发重入（上次未结束会跳过错过的触发，不会堆积）。
/// </summary>
public interface IScheduledTask
{
    /// <summary>cron 表达式（如 <c>0 0 * * *</c> 每小时、<c>*/5 * * * *</c> 每 5 分钟）。</summary>
    string CronExpression { get; }

    /// <summary>执行任务。</summary>
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}
