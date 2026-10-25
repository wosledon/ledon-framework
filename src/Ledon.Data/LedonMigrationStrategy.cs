namespace Ledon.Data;

/// <summary>启动时的库结构策略。</summary>
public enum LedonMigrationStrategy
{
    /// <summary>
    /// 自动（默认）：有挂起迁移则 <c>Migrate</c>，否则 <c>EnsureCreated</c>。
    /// 开发与大多数自托管场景直接用这个。
    /// </summary>
    Auto = 0,

    /// <summary>只走迁移（有迁移才建结构；没有迁移什么都不做）。</summary>
    MigrateOnly = 1,

    /// <summary>只 EnsureCreated（忽略迁移，按模型直接建库；模型变了需要删库重建）。</summary>
    EnsureCreatedOnly = 2,

    /// <summary>启动完全不碰库结构（结构由 CI/部署流水线或 DBA 管理）。</summary>
    None = 3,
}
