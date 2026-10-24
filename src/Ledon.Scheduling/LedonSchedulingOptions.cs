using Cronos;

namespace Ledon.Scheduling;

/// <summary>调度默认参数。</summary>
public sealed class LedonSchedulingOptions
{
    /// <summary>cron 表达式所用时区，默认 UTC。</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>
    /// 错过触发补偿（默认关）。同一任务的执行天然串行（上次未结束不会并发重跑）；
    /// 当执行耗时跨过后续触发点时：
    /// <list type="bullet">
    ///   <item><c>false</c>（默认）：错过的触发点被丢弃，下一次执行为"执行完毕后"的下一个未来触发点；</item>
    ///   <item><c>true</c>：执行完毕后立即补跑错过的触发（逐个、仍串行），追平后恢复正常节奏。</item>
    /// </list>
    /// 执行时间短于触发间隔时两种设置无差异。
    /// </summary>
    public bool CatchUpMissedTriggers { get; set; }
}
