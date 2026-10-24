namespace Ledon.Workflow;

/// <summary>
/// 工作流步骤的执行结果：决定下一步走向哪个步骤。
/// </summary>
public enum StepOutcome
{
    /// <summary>成功：按定义的转移进入下一步（无转移即完成）。</summary>
    Success,

    /// <summary>失败：进入重试；重试耗尽后触发补偿（若定义）或标记工作流失败。</summary>
    Failure,
}

/// <summary>
/// 工作流步骤契约。实现类被自动发现（允许列表扫描）——零注册接入。
/// 步骤是无状态的单例：每次执行拿到 <see cref="StepContext"/>（含实例数据与取消令牌）。
/// </summary>
public interface IWorkflowStep
{
    /// <summary>步骤名（稳定标识：转移与历史记录都用它，改名等于换步骤）。</summary>
    string Name { get; }

    /// <summary>执行步骤。</summary>
    Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// 可补偿步骤：失败回滚时 <see cref="CompensateAsync"/> 被调用（Saga 模式）。
/// 实现此接口即参与补偿；不实现则步骤本身不可回滚（其副作用需业务自行处理）。
/// </summary>
public interface ICompensableStep : IWorkflowStep
{
    /// <summary>补偿（撤销本步骤已产生的副作用）。应对重复调用幂等。</summary>
    Task CompensateAsync(StepContext context, CancellationToken cancellationToken = default);
}

/// <summary>步骤执行上下文：当前实例、数据与依赖。</summary>
public sealed class StepContext
{
    public StepContext(
        WorkflowInstance instance,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        Instance = instance;
        Services = services;
        CancellationToken = cancellationToken;
    }

    /// <summary>正在执行的工作流实例。</summary>
    public WorkflowInstance Instance { get; }

    /// <summary>请求的服务提供器（作用域与实例一致）。</summary>
    public IServiceProvider Services { get; }

    /// <summary>取消令牌（宿主持停/取消时触发）。</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>实例数据（跨步骤共享的字典，随实例持久化）。</summary>
    public IDictionary<string, object?> Data => Instance.Data;

    /// <summary>当前步骤名。</summary>
    public string CurrentStep => Instance.CurrentStep ?? string.Empty;
}
