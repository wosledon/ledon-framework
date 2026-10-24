using System.Diagnostics.Metrics;

namespace Ledon.Scheduling;

/// <summary>
/// 定时任务指标（OpenTelemetry Meter <c>Ledon.Scheduling</c>）。
/// 由 <c>AddLedonObservability()</c> 的 MeterProvider 自动收集，也可自行用 MeterListener 消费。
/// </summary>
public static class SchedulerMetrics
{
    /// <summary>Meter 名。</summary>
    public const string MeterName = "Ledon.Scheduling";

    /// <summary>调度指标 Meter。</summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>任务执行次数（tag：task、status=success/failed）。</summary>
    public static readonly Counter<long> TaskExecutions =
        Meter.CreateCounter<long>("ledon.scheduler.task.executions", "次", "定时任务执行次数");

    /// <summary>执行耗时跨过触发点、被丢弃的触发次数（tag：task）。</summary>
    public static readonly Counter<long> MissedTriggers =
        Meter.CreateCounter<long>("ledon.scheduler.task.missed_triggers", "次", "执行耗时跨过触发点被丢弃的触发次数");

    /// <summary>手动触发（重放）执行次数（tag：task）。</summary>
    public static readonly Counter<long> ManualInvocations =
        Meter.CreateCounter<long>("ledon.scheduler.task.manual_invocations", "次", "经 ISchedulerInvoker 的手动触发次数");

    /// <summary>领导权让出次数（多实例部署，tag：reason）。</summary>
    public static readonly Counter<long> LeadershipLost =
        Meter.CreateCounter<long>("ledon.scheduler.leadership.lost", "次", "多实例部署下领导权让出次数");
}

/// <summary>一次定时任务执行的记录。</summary>
public sealed class ScheduledTaskExecution
{
    /// <summary>任务名（实现类名）。</summary>
    public required string TaskName { get; set; }

    /// <summary>执行起始时间（UTC）。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>执行耗时。</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>失败信息（成功时为 null）。</summary>
    public string? Error { get; set; }

    /// <summary>是否手动触发（false = cron 触发）。</summary>
    public bool Manual { get; set; }
}

/// <summary>
/// 定时任务执行历史（进程内有界环形缓冲，默认保留最近 100 条）。
/// 用途：运维排障（"上次跑成功了吗、耗时多久"）、手动重放后确认结果。
/// 多实例部署时各实例只记自己的执行——需要全局视图请接可观测性后端的 span/指标。
/// </summary>
public interface IScheduledTaskExecutionHistory
{
    /// <summary>全部记录（新→旧）。</summary>
    IReadOnlyList<ScheduledTaskExecution> Snapshot();

    /// <summary>指定任务的最近记录（新→旧）。</summary>
    IReadOnlyList<ScheduledTaskExecution> Get(string taskName, int maxCount = 20);

    /// <summary>最近一次执行（没有则为 null）。</summary>
    ScheduledTaskExecution? Latest(string taskName);

    /// <summary>记录一次执行。</summary>
    void Record(ScheduledTaskExecution execution);
}

/// <summary>执行历史的默认实现：有界环形缓冲（线程安全）。</summary>
internal sealed class ScheduledTaskExecutionHistory(int capacity = 100) : IScheduledTaskExecutionHistory
{
    private readonly object _gate = new();
    private readonly List<ScheduledTaskExecution> _entries = [];

    public void Record(ScheduledTaskExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        lock (_gate)
        {
            _entries.Insert(0, execution);

            if (_entries.Count > capacity)
                _entries.RemoveRange(capacity, _entries.Count - capacity);
        }
    }

    public IReadOnlyList<ScheduledTaskExecution> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    public IReadOnlyList<ScheduledTaskExecution> Get(string taskName, int maxCount = 20)
    {
        lock (_gate)
            return _entries
                .Where(execution => string.Equals(execution.TaskName, taskName, StringComparison.Ordinal))
                .Take(maxCount)
                .ToArray();
    }

    public ScheduledTaskExecution? Latest(string taskName)
    {
        lock (_gate)
            return _entries.FirstOrDefault(execution =>
                string.Equals(execution.TaskName, taskName, StringComparison.Ordinal));
    }
}
