namespace Ledon.MultiTenancy;

/// <summary>
/// 当前租户上下文。Web 请求取中间件解析的值；
/// 后台任务（队列/调度）经 <see cref="TenantContext.SetAmbient"/> 穿透
/// （入队时捕获、执行时恢复），避免后台工作丢租户。
/// </summary>
public interface ITenantContext
{
    /// <summary>当前租户 ID；无法解析时为 null（单租户模式）。</summary>
    string? TenantId { get; }

    /// <summary>是否处于多租户上下文（已解析出租户）。</summary>
    bool IsMultiTenant => TenantId is not null;
}
