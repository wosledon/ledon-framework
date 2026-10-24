namespace Ledon.Data;

/// <summary>数据层开关。默认自动建库/迁移，无需任何配置。</summary>
public sealed class LedonDataOptions
{
    /// <summary>
    /// 启动时自动建库/迁移（默认开）：存在挂起迁移则 <c>MigrateAsync</c>，
    /// 否则 <c>EnsureCreatedAsync</c>。自己管理 schema 时可关闭。
    /// </summary>
    public bool AutoMigrate { get; set; } = true;
}
