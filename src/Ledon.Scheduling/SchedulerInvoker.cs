using Ledon.Module;
using Ledon.Module.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Scheduling;

/// <summary>
/// 定时任务手动触发（重放）：等不到下一个 cron 触发点时立即执行一次。
/// 典型用途：运维端点（"立刻重跑失败的对账任务"）、上线后预热任务。
/// 执行在独立作用域中进行，与 cron 触发走同一套历史记录与指标。
/// </summary>
public interface ISchedulerInvoker
{
    /// <summary>已发现的定时任务名（实现类名）。</summary>
    IReadOnlyList<string> TaskNames { get; }

    /// <summary>按任务名触发（大小写不敏感）。未找到返回 false。</summary>
    Task<bool> InvokeAsync(string taskName, CancellationToken cancellationToken = default);

    /// <summary>按任务类型触发。</summary>
    Task InvokeAsync<TTask>(CancellationToken cancellationToken = default) where TTask : IScheduledTask;
}

/// <summary><see cref="ISchedulerInvoker"/> 默认实现。</summary>
internal sealed class SchedulerInvoker : ISchedulerInvoker
{
    private readonly IServiceProvider _services;
    private readonly Lazy<IReadOnlyList<IScheduledTask>> _tasks;
    private readonly IScheduledTaskExecutionHistory _history;

    public SchedulerInvoker(IServiceProvider services, IScheduledTaskExecutionHistory history)
    {
        _services = services;
        _history = history;
        _tasks = new Lazy<IReadOnlyList<IScheduledTask>>(
            () => _services.GetServices<IScheduledTask>().ToArray());
    }

    public IReadOnlyList<string> TaskNames =>
        _tasks.Value.Select(task => task.GetType().Name).ToArray();

    public async Task<bool> InvokeAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var task = _tasks.Value.FirstOrDefault(candidate =>
            string.Equals(candidate.GetType().Name, taskName, StringComparison.OrdinalIgnoreCase));

        if (task is null)
            return false;

        await InvokeCoreAsync(task, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task InvokeAsync<TTask>(CancellationToken cancellationToken = default) where TTask : IScheduledTask
    {
        var task = _tasks.Value.FirstOrDefault(candidate => candidate is TTask)
            ?? throw new InvalidOperationException($"未发现定时任务 {typeof(TTask).Name}（确认它已被扫描到且未被 [DisableAutoDiscovery] 排除）");

        return InvokeCoreAsync(task, cancellationToken);
    }

    private async Task InvokeCoreAsync(IScheduledTask task, CancellationToken cancellationToken)
    {
        var name = task.GetType().Name;
        var started = DateTimeOffset.UtcNow;
        Exception? failure = null;

        using var activity = LedonActivity.Modules.StartActivity($"ScheduledTask.{name}.Manual");
        activity?.SetTag("task.name", name);
        activity?.SetTag("task.manual", true);

        try
        {
            await task.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            activity?.SetTag("error", ex.Message);
            throw;
        }
        finally
        {
            SchedulerMetrics.ManualInvocations.Add(1,
                new KeyValuePair<string, object?>("task", name),
                new KeyValuePair<string, object?>("status", failure is null ? "success" : "failed"));

            _history.Record(new ScheduledTaskExecution
            {
                TaskName = name,
                StartedAt = started,
                Duration = DateTimeOffset.UtcNow - started,
                Success = failure is null,
                Error = failure?.Message,
                Manual = true,
            });
        }
    }
}
