namespace Ledon.AuditLog;

/// <summary>变更动作。</summary>
public enum AuditAction
{
    /// <summary>新增。</summary>
    Created = 0,

    /// <summary>修改。</summary>
    Updated = 1,

    /// <summary>删除（含软删除转换）。</summary>
    Deleted = 2,
}

/// <summary>一条审计流水：谁、何时、对哪个实体、做了什么、改了什么。</summary>
public sealed class AuditEntry
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>发生时间（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>实体类型名。</summary>
    public string EntityType { get; set; } = "";

    /// <summary>实体主键（字符串化，兼容复合/非 GUID 键）。</summary>
    public string EntityId { get; set; } = "";

    /// <summary>动作。</summary>
    public AuditAction Action { get; set; }

    /// <summary>属性级变更 JSON：<c>[{ "property": "Price", "before": 10, "after": 12 }]</c>。</summary>
    public string? Changes { get; set; }

    /// <summary>操作人标识。</summary>
    public string? UserId { get; set; }

    /// <summary>操作人名。</summary>
    public string? UserName { get; set; }

    /// <summary>请求/关联 ID（与链路追踪对齐）。</summary>
    public string? RequestId { get; set; }

    /// <summary>来源 IP。</summary>
    public string? IpAddress { get; set; }
}

/// <summary>属性级变更。</summary>
public sealed record PropertyChange(string Property, object? Before, object? After);

/// <summary>审计存储（写 + 查 + 清理）。</summary>
public interface IAuditStore
{
    /// <summary>写入一条。</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>按实体查最近记录（倒序）。</summary>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(
        string? entityType = null, string? entityId = null, int count = 50, CancellationToken cancellationToken = default);

    /// <summary>清理早于指定时间的记录（保留策略），返回删除条数。</summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);
}
