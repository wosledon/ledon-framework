namespace Ledon.Data;

/// <summary>
/// 带审计字段的实体：创建/修改时间与操作人由 <see cref="LedonAuditInterceptor"/> 自动填充
/// （<c>AddLedonData(o =&gt; o.EnableAuditing = true)</c> 后生效，无需在业务代码里赋值）。
/// </summary>
public interface IAudited
{
    /// <summary>创建时间（UTC）。</summary>
    DateTime CreatedAt { get; set; }

    /// <summary>创建者标识（用户 Id/API Key 持有者；未认证上下文为 null）。</summary>
    string? CreatedBy { get; set; }

    /// <summary>最后修改时间（UTC）。</summary>
    DateTime? ModifiedAt { get; set; }

    /// <summary>最后修改者标识。</summary>
    string? ModifiedBy { get; set; }
}

/// <summary>
/// 软删除实体：删除操作被转换为更新（<c>IsDeleted = true</c>），行不出库。
/// 查询过滤需显式启用（见 <see cref="ModelBuilderExtensions.ApplySoftDeleteFilters"/>）——
/// 与多租户一致的设计边界：隔离/过滤显式声明，不隐式生效。
/// </summary>
public interface ISoftDelete
{
    /// <summary>是否已删除。</summary>
    bool IsDeleted { get; set; }
}

/// <summary>
/// 审计操作人来源（可选）：注册实现后，审计拦截器用它填充 <see cref="IAudited.CreatedBy"/>/
/// <see cref="IAudited.ModifiedBy"/>。不注册时只填时间戳（操作人为 null）。
/// Web 场景可用 <c>Ledon.Security</c> 的 <c>ICurrentUser</c> 适配。
/// </summary>
public interface IAuditUserAccessor
{
    /// <summary>当前操作人标识；匿名上下文返回 null。</summary>
    string? CurrentUserId { get; }
}
