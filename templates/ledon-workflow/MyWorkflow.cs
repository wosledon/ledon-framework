using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.MyWorkflow;

/// <summary>
/// 业务模块：实现 IModule 即被宿主自动发现（宿主引用本程序集即可，零注册）。
/// </summary>
public class MyWorkflowModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("MyWorkflowModule 初始化（工作流定义/步骤来自本程序集）");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 工作流定义：步骤清单 + 转移表。实现类被自动发现，按 Name 引用。
/// 多出口（如 ship/cancel）由步骤写 ctx.Data["next"] 选择。
/// </summary>
public class MyWorkflow : IWorkflowDefinition
{
    public string Name => "my-workflow";
    public string InitialStep => "validate";

    public IReadOnlyDictionary<string, string[]> Transitions => new Dictionary<string, string[]>
    {
        ["validate"] = ["process"],
        ["process"] = ["finish"],
    };
}

/// <summary>步骤一：校验。</summary>
public class ValidateStep : IWorkflowStep
{
    public string Name => "validate";

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default)
    {
        context.Logger();      // 可选：写日志
        context.Data["validated"] = true;
        return Task.FromResult(StepOutcome.Success);
    }
}

/// <summary>步骤二：处理（可补偿——失败回滚时撤销副作用）。</summary>
public class ProcessStep : IWorkflowStep, ICompensableStep
{
    public string Name => "process";

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default)
    {
        context.Data["processed"] = true;
        return Task.FromResult(StepOutcome.Success);
    }

    /// <summary>补偿：撤销本步骤已产生的副作用（应对重复调用幂等）。</summary>
    public Task CompensateAsync(StepContext context, CancellationToken cancellationToken = default)
    {
        context.Data["processed"] = false;
        return Task.CompletedTask;
    }
}

/// <summary>步骤三：完成（终止步骤——没有出口即结束）。</summary>
public class FinishStep : IWorkflowStep
{
    public string Name => "finish";

    public Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(StepOutcome.Success);
}

/// <summary>StepContext 的日志扩展（模板便利方法）。</summary>
public static class StepContextExtensions
{
    public static void Logger(this StepContext context) =>
        context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Ledon.MyWorkflow")
            .LogInformation("执行步骤 {Step}（实例 {Instance}）", context.CurrentStep, context.Instance.Id);
}
