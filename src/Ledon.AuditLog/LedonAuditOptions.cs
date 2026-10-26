namespace Ledon.AuditLog;

/// <summary>审计选项。</summary>
public sealed class LedonAuditOptions
{
    /// <summary>记录新增，默认 <c>true</c>。</summary>
    public bool AuditCreated { get; set; } = true;

    /// <summary>记录修改，默认 <c>true</c>。</summary>
    public bool AuditUpdated { get; set; } = true;

    /// <summary>记录删除，默认 <c>true</c>。</summary>
    public bool AuditDeleted { get; set; } = true;

    /// <summary>保留天数（0 = 不自动清理），默认 90。</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>清理巡检间隔，默认 1 小时（仅 <see cref="RetentionDays"/> &gt; 0 时生效）。</summary>
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromHours(1);
}
