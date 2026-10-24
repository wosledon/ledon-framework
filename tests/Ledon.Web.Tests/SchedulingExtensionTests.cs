using System.Diagnostics.Metrics;
using Ledon.Scheduling;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 调度扩展测试：执行历史（运维排障）、手动触发（等不到 cron 时立即重跑）、
/// 指标（OTel Meter Ledon.Scheduling）。
/// </summary>
public class SchedulingExtensionTests : IDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonScheduling();

        var app = builder.Build();
        _app = app;
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Scheduler_records_execution_history_after_cron_run()
    {
        var app = await StartAsync();

        var task = app.Services.GetServices<IScheduledTask>().OfType<EverySecondTask>().Single();
        await task.Fired.WaitAsync(TimeSpan.FromSeconds(5));

        var history = app.Services.GetRequiredService<IScheduledTaskExecutionHistory>();
        var latest = history.Latest(nameof(EverySecondTask));

        Assert.NotNull(latest);
        Assert.True(latest.Success);
        Assert.False(latest.Manual);
        Assert.True(latest.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Scheduler_invoker_runs_task_by_name_and_by_type()
    {
        var app = await StartAsync();

        var invoker = app.Services.GetRequiredService<ISchedulerInvoker>();

        // 已发现的任务清单
        Assert.Contains(nameof(ManualTriggerTask), invoker.TaskNames);

        // 按名称触发（大小写不敏感）
        Assert.True(await invoker.InvokeAsync("manualtriggertask", CancellationToken.None));

        var task = app.Services.GetServices<IScheduledTask>().OfType<ManualTriggerTask>().Single();
        await task.Executed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, task.Invocations);

        // 按类型触发
        await invoker.InvokeAsync<ManualTriggerTask>(CancellationToken.None);
        Assert.Equal(2, task.Invocations);

        // 未注册的任务名：false 而不是抛异常
        Assert.False(await invoker.InvokeAsync("NoSuchTask", CancellationToken.None));

        // 历史记录标记为手动触发
        var history = app.Services.GetRequiredService<IScheduledTaskExecutionHistory>();
        var latest = history.Latest(nameof(ManualTriggerTask));
        Assert.NotNull(latest);
        Assert.True(latest.Manual);
    }

    [Fact]
    public async Task Scheduler_reports_execution_metrics()
    {
        long manual = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == SchedulerMetrics.MeterName
                && instrument.Name is "ledon.scheduler.task.manual_invocations" or "ledon.scheduler.task.executions")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "task" && Equals(tag.Value, nameof(ManualTriggerTask)))
                    Interlocked.Add(ref manual, value);
            }
        });
        listener.Start();

        var app = await StartAsync();
        var invoker = app.Services.GetRequiredService<ISchedulerInvoker>();
        await invoker.InvokeAsync<ManualTriggerTask>(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref manual) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(manual >= 1, "未观测到 ledon.scheduler.task.manual_invocations 指标");
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}
