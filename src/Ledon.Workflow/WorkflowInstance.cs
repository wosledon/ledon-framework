namespace Ledon.Workflow;

/// <summary>
/// 工作流定义：步骤清单 + 转移表。实现类被自动发现，按 <see cref="Name"/> 引用。
/// <code>
/// public class OrderWorkflow : IWorkflowDefinition
/// {
///     public string Name =&gt; "order";
///     public string InitialStep =&gt; "reserve";
///     public IReadOnlyDictionary&lt;string, string[]&gt; Transitions =&gt; new()
///     {
///         ["reserve"] = ["charge"],
///         ["charge"] = ["ship", "cancel"],   // 多个出口：由步骤数据里的 "next" 决定
///     };
/// }
/// </code>
/// </summary>
public interface IWorkflowDefinition
{
    /// <summary>工作流名（稳定标识）。</summary>
    string Name { get; }

    /// <summary>起始步骤名。</summary>
    string InitialStep { get; }

    /// <summary>转移表：步骤名 → 可达的下一步集合（空/缺失 = 终止步骤）。</summary>
    IReadOnlyDictionary<string, string[]> Transitions { get; }
}

/// <summary>工作流实例状态。</summary>
public enum WorkflowStatus
{
    /// <summary>已创建，未开始执行。</summary>
    Created,

    /// <summary>执行中。</summary>
    Running,

    /// <summary>已成功完成。</summary>
    Completed,

    /// <summary>失败（重试耗尽且补偿后仍失败，或不可补偿）。</summary>
    Failed,

    /// <summary>已补偿回滚。</summary>
    Compensated,
}

/// <summary>
/// 工作流实例（运行时视图）。持久化由 <see cref="IWorkflowStore"/> 负责，
/// 引擎每次推进后保存——进程重启后可从存储恢复继续执行。
/// </summary>
public sealed class WorkflowInstance
{
    private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

    /// <summary>实例 ID。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>工作流定义名。</summary>
    public string WorkflowName { get; set; } = string.Empty;

    /// <summary>当前步骤名（null = 尚未开始）。</summary>
    public string? CurrentStep { get; set; }

    /// <summary>实例状态。</summary>
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Created;

    /// <summary>当前步骤已重试次数。</summary>
    public int RetryCount { get; set; }

    /// <summary>最近一次错误信息（失败时）。</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 乐观并发令牌。每次保存由存储生成新值，UPDATE 时作为 WHERE 条件——
    /// 两个节点同时改同一实例时后提交的那个会拿到
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>，
    /// 而不是静默覆盖对方刚写入的 <see cref="CompletedSteps"/>（丢更新）。
    /// </summary>
    public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>当前持有租约的节点标识（恢复服务认领用）。</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>租约到期时间（UTC）；null 或已过期 = 可被重新认领。</summary>
    public DateTimeOffset? ClaimedUntilUtc { get; set; }

    /// <summary>跨步骤共享数据（随实例持久化）。</summary>
    public IDictionary<string, object?> Data => _data;

    /// <summary>已执行完成的步骤名（按顺序，含补偿信息）。</summary>
    public List<string> CompletedSteps { get; } = [];
}

/// <summary>工作流运行选项。</summary>
public sealed class WorkflowOptions
{
    /// <summary>单步骤最大重试次数，默认 3。</summary>
    public int MaxRetriesPerStep { get; set; } = 3;
}
