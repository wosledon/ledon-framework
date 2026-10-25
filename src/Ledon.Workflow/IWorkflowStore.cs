namespace Ledon.Workflow;

/// <summary>
/// 工作流存储抽象：保存/读取实例（含数据），供引擎推进与重启恢复。
/// 默认实现 <c>Ledon.Workflow</c> 的 EF 版本（<c>AddLedonWorkflow</c> 注册）。
/// </summary>
public interface IWorkflowStore
{
    /// <summary>保存实例（不存在则创建）。</summary>
    Task SaveAsync(WorkflowInstance instance, CancellationToken cancellationToken = default);

    /// <summary>按 ID 读取实例；不存在返回 null。</summary>
    Task<WorkflowInstance?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出可恢复的实例：Running 且租约已过期/未认领。
    /// 按更新时间排序，取前 <paramref name="batchSize"/> 个。
    /// </summary>
    Task<IReadOnlyList<WorkflowInstance>> GetResumableAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子认领一个实例（多节点部署的关键）：把 <see cref="WorkflowInstance.ClaimedBy"/>
    /// 设为自己、租约延到 <paramref name="lease"/> 之后。
    /// <para>
    /// 只有第一个认领的节点拿到 <c>true</c>——否则每个节点都会恢复同一批实例，
    /// 同一个步骤被并发执行两遍（重复副作用）。
    /// </para>
    /// </summary>
    Task<bool> ClaimAsync(
        Guid instanceId,
        string owner,
        TimeSpan lease,
        CancellationToken cancellationToken = default);
}
