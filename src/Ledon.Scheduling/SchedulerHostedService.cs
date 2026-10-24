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
        // 立即解析一次任务清单（此时容器已构建，模块已注册）
        var tasks = _services.GetServices<IScheduledTask>().ToList();
        if (tasks.Count == 0)
            return;

        _logger.LogInformation("调度器启动：{Count} 个定时任务", tasks.Count);

        // 每个任务一个独立循环（按各自 cron 驱动）
        await Task.WhenAll(tasks.Select(task => RunLoopAsync(task, stoppingToken)));
    }

    private async Task RunLoopAsync(IScheduledTask task, CancellationToken stoppingToken)
    {
        var name = task.GetType().Name;

        // 6 段（含秒）与 5 段都要支持：Cronos 默认只解析 5 段，6 段需显式 IncludeSeconds
        var fields = task.CronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        if (!CronExpression.TryParse(task.CronExpression, format, out var cron))
        {
            // 启动即失败：cron 写错不应该拖到运行时才静默不执行
            throw new InvalidOperationException(
                $"定时任务 {name} 的 cron 表达式无效：'{task.CronExpression}'");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = cron.GetNextOccurrence(now, _options.TimeZone);
            if (next is null)
                return;

            var delay = next.Value - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);

            if (stoppingToken.IsCancellationRequested)
                return;

            await using var scope = _services.CreateAsyncScope();

            using var activity = LedonActivity.Modules.StartActivity($"ScheduledTask.{name}");
            activity?.SetTag("task.name", name);
            activity?.SetTag("task.cron", task.CronExpression);

            try
            {
                await task.ExecuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "定时任务 {Task} 执行失败（已跳过本次触发）", name);
            }
        }
    }
}
