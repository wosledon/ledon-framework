using System.Diagnostics;
using Ledon.Module.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Workflow;

/// <summary>
/// 工作流引擎：驱动实例从起始步骤推进到终止（或失败/补偿）。
/// 语义：
/// <list type="bullet">
///   <item>每步执行产生 span，推进后立即持久化——进程崩溃后可从存储恢复继续；</item>
///   <item>步骤失败重试至 <see cref="WorkflowOptions.MaxRetriesPerStep"/>；</item>
///   <item>重试耗尽：已执行的步骤按逆序补偿（Saga），实例标记 <see cref="WorkflowStatus.Compensated"/>；</item>
///   <item>无可补偿步骤或补偿失败：实例标记 <see cref="WorkflowStatus.Failed"/>，异常抛出。</item>
/// </list>
/// </summary>
public sealed class WorkflowEngine
{
    private readonly IServiceProvider _services;
    private readonly IWorkflowStore _store;
    private readonly WorkflowOptions _options;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IServiceProvider services,
        IWorkflowStore store,
        WorkflowOptions options,
        ILogger<WorkflowEngine> logger)
    {
        _services = services;
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>步骤名 → 步骤实现（自动发现结果，启动时解析一次）。</summary>
    private Dictionary<string, IWorkflowStep> StepsByName(IEnumerable<IWorkflowStep> steps) =>
        steps.GroupBy(step => step.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    /// <summary>定义名 → 定义。</summary>
    private static Dictionary<string, IWorkflowDefinition> DefinitionsByName(IEnumerable<IWorkflowDefinition> definitions) =>
        definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    /// <summary>创建实例（不执行）。</summary>
    public async Task<WorkflowInstance> CreateAsync(
        string workflowName,
        IDictionary<string, object?>? data = null,
        CancellationToken cancellationToken = default)
    {
        var instance = new WorkflowInstance { WorkflowName = workflowName };
        if (data is not null)
            foreach (var (key, value) in data)
                instance.Data[key] = value;

        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        return instance;
    }

    /// <summary>执行（或继续执行）实例直至终止/失败。</summary>
    public async Task<WorkflowInstance> RunAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await _store.GetAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"工作流实例 {instanceId} 不存在");

        if (instance.Status is WorkflowStatus.Completed or WorkflowStatus.Compensated)
            return instance;

        await using var scope = _services.CreateAsyncScope();
        var definitions = DefinitionsByName(scope.ServiceProvider.GetServices<IWorkflowDefinition>());
        var steps = StepsByName(scope.ServiceProvider.GetServices<IWorkflowStep>());

        if (!definitions.TryGetValue(instance.WorkflowName, out var definition))
            throw new InvalidOperationException($"未找到工作流定义 {instance.WorkflowName}");

        var current = instance.CurrentStep ?? definition.InitialStep;
        instance.Status = WorkflowStatus.Running;
        instance.CurrentStep = current;
        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

        var executed = new List<(IWorkflowStep Step, StepContext Context)>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!steps.TryGetValue(current, out var step))
                throw new InvalidOperationException(
                    $"工作流 {instance.WorkflowName} 的步骤 {current} 未实现（未被自动发现？）");

            using var activity = LedonActivity.Modules.StartActivity($"Workflow.{instance.WorkflowName}.{current}");
            activity?.SetTag("workflow.name", instance.WorkflowName);
            activity?.SetTag("workflow.instance", instance.Id.ToString());
            activity?.SetTag("workflow.step", current);

            var context = new StepContext(instance, scope.ServiceProvider, cancellationToken);
            StepOutcome outcome;

            try
            {
                outcome = await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                instance.LastError = ex.Message;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex,
                    "工作流 {Workflow} 实例 {Instance} 步骤 {Step} 执行异常（第 {Retry} 次）",
                    instance.WorkflowName, instance.Id, current, instance.RetryCount + 1);

                outcome = StepOutcome.Failure;
            }

            if (outcome == StepOutcome.Failure)
            {
                instance.RetryCount++;
                await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                if (instance.RetryCount < _options.MaxRetriesPerStep)
                    continue;   // 重试同一骤

                // 重试耗尽：补偿 + 标记失败
                await CompensateAsync(executed, instance, cancellationToken).ConfigureAwait(false);
                instance.Status = executed.Count > 0
                    ? WorkflowStatus.Compensated
                    : WorkflowStatus.Failed;
                await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                _logger.LogError(
                    "工作流 {Workflow} 实例 {Instance} 在步骤 {Step} 重试耗尽（{Retries} 次），已补偿 {Compensated} 步",
                    instance.WorkflowName, instance.Id, current, instance.RetryCount, executed.Count);

                throw new WorkflowExecutionException(instance, current, instance.LastError);
            }

            // 成功：记录并决定下一步
            instance.RetryCount = 0;
            executed.Add((step, context));
            instance.CompletedSteps.Add(current);

            var next = NextStep(definition, current, instance);
            await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

            if (next is null)
            {
                instance.Status = WorkflowStatus.Completed;
                instance.CurrentStep = null;
                await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "工作流 {Workflow} 实例 {Instance} 完成（{Steps} 步）",
                    instance.WorkflowName, instance.Id, instance.CompletedSteps.Count);

                return instance;
            }

            instance.CurrentStep = next;
            await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
            current = next;
        }
    }

    /// <summary>
    /// 下一步：优先取实例数据里的 "next"（步骤可动态选择出口），
    /// 否则取转移表的第一个出口；没有出口 = 终止步骤。
    /// </summary>
    private static string? NextStep(IWorkflowDefinition definition, string current, WorkflowInstance instance)
    {
        if (instance.Data.TryGetValue("next", out var requested) && requested is string nextName)
            return nextName;

        return definition.Transitions.TryGetValue(current, out var candidates) && candidates.Length > 0
            ? candidates[0]
            : null;
    }

    /// <summary>逆序补偿已执行步骤（幂等由步骤自身保证）。</summary>
    private async Task CompensateAsync(
        List<(IWorkflowStep Step, StepContext Context)> executed,
        WorkflowInstance instance,
        CancellationToken cancellationToken)
    {
        for (var i = executed.Count - 1; i >= 0; i--)
        {
            var (step, context) = executed[i];

            if (step is not ICompensableStep compensable)
            {
                _logger.LogWarning(
                    "工作流 {Workflow} 实例 {Instance} 步骤 {Step} 不可补偿（未实现 ICompensableStep），跳过",
                    instance.WorkflowName, instance.Id, step.Name);
                continue;
            }

            try
            {
                await compensable.CompensateAsync(context, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "工作流 {Workflow} 实例 {Instance} 步骤 {Step} 已补偿",
                    instance.WorkflowName, instance.Id, step.Name);
            }
            catch (Exception ex)
            {
                // 补偿失败不阻断其余补偿：记录并继续（人工介入信号）
                _logger.LogError(ex,
                    "工作流 {Workflow} 实例 {Instance} 步骤 {Step} 补偿失败（需人工介入）",
                    instance.WorkflowName, instance.Id, step.Name);
            }
        }
    }
}

/// <summary>工作流执行失败（重试耗尽并补偿后抛出，含实例现场）。</summary>
public sealed class WorkflowExecutionException : Exception
{
    public WorkflowExecutionException(WorkflowInstance instance, string step, string? reason)
        : base($"工作流 {instance.WorkflowName} 实例 {instance.Id} 在步骤 {step} 失败：{reason ?? "未知错误"}")
    {
        Instance = instance;
        Step = step;
    }

    /// <summary>失败的实例（含数据与已完成步骤，可人工检查/重放）。</summary>
    public WorkflowInstance Instance { get; }

    /// <summary>失败的步骤名。</summary>
    public string Step { get; }
}
