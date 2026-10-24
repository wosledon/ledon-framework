using Cronos;

namespace Ledon.Scheduling;

/// <summary>调度默认参数。</summary>
public sealed class LedonSchedulingOptions
{
    /// <summary>cron 表达式所用时区，默认 UTC。</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;
}
