using System.Collections.Concurrent;

namespace Ledon.Workflow;

/// <summary>
/// 进程内工作流存储（未配 WorkflowDbContext 时的兜底）：实例存活于进程内存，
/// 重启后丢失——需要持久化与重启恢复时让 DbContext 继承 <c>WorkflowDbContext</c>。
/// </summary>
internal sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly ConcurrentDictionary<Guid, WorkflowInstance> _instances = new();

    public Task SaveAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instances[instance.Id] = instance;
        return Task.CompletedTask;
    }

    public Task<WorkflowInstance?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_instances.TryGetValue(id, out var instance) ? instance : null);

    public Task<IReadOnlyList<WorkflowInstance>> GetResumableAsync(
        int batchSize,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkflowInstance>>(
            _instances.Values
                .Where(instance => instance.Status == WorkflowStatus.Running)
                .Take(batchSize)
                .ToArray());
}
