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

    /// <summary>列出可恢复的实例（Running 状态——进程重启后由恢复服务继续推进）。</summary>
    Task<IReadOnlyList<WorkflowInstance>> GetResumableAsync(int batchSize, CancellationToken cancellationToken = default);
}
