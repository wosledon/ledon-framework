namespace Ledon.Data;

/// <summary>数据层开关。默认自动建库/迁移，无需任何配置。</summary>
public sealed class LedonDataOptions
{
    /// <summary>
    /// 启动时自动建库/迁移（默认开）：存在挂起迁移则 <c>MigrateAsync</c>，
    /// 否则 <c>EnsureCreatedAsync</c>。自己管理 schema 时可关闭。
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// 审计填充（默认关）：开启后 <see cref="IAudited"/> 实体的创建/修改时间戳与操作人
    /// （<see cref="IAuditUserAccessor"/>）由拦截器自动写入，业务代码不再手写。
    /// </summary>
    public bool EnableAuditing { get; set; }

    /// <summary>
    /// 软删除转换（默认开）：<see cref="ISoftDelete"/> 实体的删除操作转换为更新
    /// （<c>IsDeleted = true</c>）。查询过滤需在 OnModelCreating 里显式
    /// <c>ApplySoftDeleteFilters()</c>（显式声明，不隐式生效）。
    /// </summary>
    public bool AutoSoftDelete { get; set; } = true;

    /// <summary>
    /// 连接弹性（默认关）：开启后为已注册的 DbContext 启用 EF 执行策略重试
    /// （瞬断恢复，重试次数 <see cref="RetryMaxAttempts"/>）。
    /// 依赖关系数据库提供 <c>EnableRetryOnFailure</c> 扩展（SqlServer/Sqlite/Npgsql 等）。
    /// </summary>
    public bool EnableRetryOnFailure { get; set; }

    /// <summary>连接弹性最大重试次数，默认 3（需 <see cref="EnableRetryOnFailure"/>）。</summary>
    public int RetryMaxAttempts { get; set; } = 3;

    /// <summary>
    /// 领域事件自动分发（默认开）：实体实现 <c>IHasDomainEvents</c>（或继承 <c>Entity</c>）后，
    /// SaveChanges 成功即经 <c>IEventBus</c> 分发其领域事件（进程内）。
    /// 只依赖进程内事件、不需要可靠投递时用它；跨服务投递用发件箱。
    /// </summary>
    public bool DispatchDomainEvents { get; set; } = true;

    /// <summary>
    /// 注册通用仓储 <see cref="IRepository{T}"/>（默认关）：按扫描到的实体自动注册。
    /// 关掉时直接注入 DbContext（框架不强制仓储层）。
    /// </summary>
    public bool UseRepositories { get; set; }
}
