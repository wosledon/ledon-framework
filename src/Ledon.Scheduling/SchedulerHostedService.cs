using System.Diagnostics;
using Cronos;
using Ledon.Module;
using Ledon.Module.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Scheduling;

/// <summary>
/// 调度宿主服务：为每个 <see cref="IScheduledTask"/> 维护一个按 cron 表达式驱动的循环，
/// 每个任务的执行都在独立 DI 作用域与 span 中进行。单个任务失败只记录日志，不影响其它任务与调度循环。
/// 同一任务的执行天然串行（循环等待上次执行结束）；执行跨过触发点时是否补跑见
/// <see cref="LedonSchedulingOptions.CatchUpMissedTriggers"/>。
/// </summary>
internal sealed class SchedulerHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly LedonSchedulingOptions _options;
    private readonly ILogger<SchedulerHostedService> _logger;

    public SchedulerHostedService(
        IServiceProvider services,
        LedonSchedulingOptions options,
        ILogger<SchedulerHostedService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var leaderLock = _services.GetService<ISchedulerLock>();
        if (leaderLock is null)
        {
            // 未配置领导权选举（默认）：单实例语义，直接调度
            await RunAllAsync(stoppingToken);
            return;
        }

        // 多实例部署：竞选循环——持有锁才调度，掉锁让出后重新竞选
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await leaderLock.AcquireAsync(stoppingToken))
            {
                _logger.LogDebug("调度领导权被其它实例持有，{Retry} 后重试", _options.LeaderLockExpiry / 3);
                await Task.Delay(_options.LeaderLockExpiry / 3, stoppingToken);
                continue;
            }

            _logger.LogInformation("本实例获得调度领导权（多实例部署下仅领导实例执行定时任务）");

            // 领导期：续约循环 + 任务循环共享取消令牌，续约失败即整体退出
            using var leadership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var renew = Task.Run(async () =>
            {
                while (!leadership.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(_options.LeaderLockExpiry / 3, stoppingToken);

                    try
                    {
                        if (await leaderLock.RenewAsync(stoppingToken))
                            continue;
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "调度领导权续约异常，让出领导权");
                    }

                    _logger.LogWarning("调度领导权续约失败（锁已被其它实例拿走），停止本实例的任务循环");
                    await leadership.CancelAsync();
                    return;
                }
            }, stoppingToken);

            try
            {
                await RunAllAsync(leadership.Token);
            }
            catch (OperationCanceledException) when (leadership.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // 领导权丢失：任务循环已退出，回到竞选循环
            }
            finally
            {
                await leaderLock.ReleaseAsync(stoppingToken);
                await leadership.CancelAsync();

                // 续约循环可能因宿主持停而取消——吞掉，不影响关停流程
                try
                {
                    await renew;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task RunAllAsync(CancellationToken cancellationToken)
    {
        // 立即解析一次任务清单（此时容器已构建，模块已注册）
        var tasks = _services.GetServices<IScheduledTask>().ToList();
        if (tasks.Count == 0)
            return;

        _logger.LogInformation("调度器启动：{Count} 个定时任务", tasks.Count);

        // 每个任务一个独立循环（按各自 cron 驱动）
        await Task.WhenAll(tasks.Select(task => RunLoopAsync(task, cancellationToken)));
    }

    private async Task RunLoopAsync(IScheduledTask task, CancellationToken stoppingToken)
    {
        var name = task.GetType().Name;
        var history = _services.GetService<IScheduledTaskExecutionHistory>();

        // 6 段（含秒）与 5 段都要支持：Cronos 默认只解析 5 段，6 段需显式 IncludeSeconds
        var fields = task.CronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        if (!CronExpression.TryParse(task.CronExpression, format, out var cron))
        {
            // 启动即失败：cron 写错不应该拖到运行时才静默不执行
            throw new InvalidOperationException(
                $"定时任务 {name} 的 cron 表达式无效：'{task.CronExpression}'");
        }

        // 上一次执行的触发点（补跑模式据此推算"下一个"发生时刻，不错过执行期间跨过的触发点）
        DateTimeOffset? lastTrigger = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            // 补跑模式：从上次执行的触发点推算下一个发生时刻（执行期间错过的触发点会立即补上）；
            // 默认模式：从当前时刻推算（错过的触发点被丢弃，等下一个未来触发点）
            var reference = _options.CatchUpMissedTriggers ? lastTrigger ?? now : now;
            var next = cron.GetNextOccurrence(reference, _options.TimeZone);
            if (next is null)
                return;

            var delay = next.Value - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
                return;

            lastTrigger = next.Value;
            await using var scope = _services.CreateAsyncScope();

            using var activity = LedonActivity.Modules.StartActivity($"ScheduledTask.{name}");
            activity?.SetTag("task.name", name);
            activity?.SetTag("task.cron", task.CronExpression);

            var started = DateTimeOffset.UtcNow;
            try
            {
                await task.ExecuteAsync(stoppingToken).ConfigureAwait(false);
                SchedulerMetrics.TaskExecutions.Add(1,
                    new KeyValuePair<string, object?>("task", name),
                    new KeyValuePair<string, object?>("status", "success"));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "定时任务 {Task} 执行失败（已跳过本次触发）", name);
                SchedulerMetrics.TaskExecutions.Add(1,
                    new KeyValuePair<string, object?>("task", name),
                    new KeyValuePair<string, object?>("status", "failed"));
            }
            finally
            {
                history?.Record(new ScheduledTaskExecution
                {
                    TaskName = name,
                    StartedAt = started,
                    Duration = DateTimeOffset.UtcNow - started,
                    Success = activity?.Status != ActivityStatusCode.Error,
                    Error = activity?.StatusDescription,
                    Manual = false,
                });
            }

            // 执行耗时跨过了下一个触发点（默认模式）：记一次错过（不补跑）
            if (!_options.CatchUpMissedTriggers)
            {
                var upcoming = cron.GetNextOccurrence(lastTrigger.Value, _options.TimeZone);
                if (upcoming is { } triggerPoint && triggerPoint < DateTimeOffset.UtcNow)
                {
                    SchedulerMetrics.MissedTriggers.Add(1, new KeyValuePair<string, object?>("task", name));
                    _logger.LogWarning(
                        "定时任务 {Task} 本次执行耗时跨过触发点 {Trigger:HH:mm:ss}（已跳过；需要补跑请开启 CatchUpMissedTriggers）",
                        name, triggerPoint);
                }
            }
        }
    }
}
