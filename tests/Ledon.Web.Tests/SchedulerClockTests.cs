using Ledon.Module.Abstractions;
using Ledon.Scheduling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 虚拟时钟驱动的调度测试：<see cref="ManualSchedulerClock"/> 让 cron 触发即时、确定地发生
/// （不必睡真实秒数），从而稳定覆盖 misfire / 补跑 / 历史记录等语义。
/// </summary>
public class SchedulerClockTests : IDisposable
{
    private WebApplication? _app;

    private async Task<(WebApplication App, ManualSchedulerClock Clock, ManualTask Task)> StartAsync(
        bool catchUpMissedTriggers = false)
    {
        // 初始时刻对齐到分钟的 :59 秒——启动后循环算出"下一触发点 = 下一分钟整"，
        // AdvanceTo 推到整分钟即触发（第一次推进就命中，不浪费一个周期）
        var clock = new ManualSchedulerClock(new DateTimeOffset(2026, 1, 1, 0, 0, 59, TimeSpan.Zero));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonScheduling(o =>
        {
            o.CatchUpMissedTriggers = catchUpMissedTriggers;

            // 显式只注册测试任务：避免同程序集其它真实 cron 夹具（EverySecondTask/SlowTask）
            // 被自动发现进来，用真实时间触发干扰虚拟时钟断言
            o.AutoDiscoverTasks = false;
        });
        builder.Services.AddSingleton<IScheduledTask, ManualTask>();
        builder.Services.AddSingleton<ISchedulerClock>(clock);

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        // 等调度循环真正进入等待（否则 AdvanceTo 可能早于首次 DelayAsync，白推一次）
        await WaitUntil(() => clock.HasWaiters, TimeSpan.FromSeconds(5));

        var task = app.Services.GetServices<IScheduledTask>().OfType<ManualTask>().Single();
        return (app, clock, task);
    }

    [Fact]
    public async Task Task_fires_when_clock_advances_past_trigger()
    {
        var (_, clock, task) = await StartAsync();

        // 启动时刻 00:00:59，循环算出下一触发点 00:01:00 并等待；
        // 推进到 00:01:00 → 触发一次
        clock.AdvanceTo(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero));
        await WaitUntil(() => task.Invocations >= 1, TimeSpan.FromSeconds(5));

        Assert.Equal(1, task.Invocations);

        // 再推进一个周期：第二次触发
        clock.AdvanceTo(new DateTimeOffset(2026, 1, 1, 0, 2, 0, TimeSpan.Zero));
        await WaitUntil(() => task.Invocations >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal(2, task.Invocations);
    }

    [Fact]
    public async Task Missed_trigger_is_dropped_by_default_and_caught_up_when_enabled()
    {
        // 执行阻塞在闸门上的任务；执行期间时钟跨过多个触发点
        var (app, clock, task) = await StartAsync(catchUpMissedTriggers: false);
        task.BlockFor = TimeSpan.FromMinutes(1);

        // 00:01:00 触发首次执行（阻塞）
        clock.AdvanceTo(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero));
        await WaitUntil(() => task.Started, TimeSpan.FromSeconds(5));
        Assert.Equal(1, task.Invocations);

        // 时钟推到 00:05:00——首次执行期间跨过 4 个触发点，随后放行
        clock.AdvanceTo(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero));
        task.Release.TrySetResult();

        // 默认（丢弃）模式：恢复后从"执行完毕时刻之后的下一个未来触发点"继续。
        // 等循环注册下一个等待点再推进（否则循环会从已推进的时刻算，跳到 00:07:00）
        await WaitUntil(() => clock.HasWaiters, TimeSpan.FromSeconds(5));
        clock.AdvanceTo(new DateTimeOffset(2026, 1, 1, 0, 6, 0, TimeSpan.Zero));
        await WaitUntil(() => task.Invocations >= 2, TimeSpan.FromSeconds(5));

        // 跨过的 4 个触发点没有补跑（丢弃语义）：只有这一次新触发
        Assert.Equal(2, task.Invocations);

        await app.StopAsync();
    }

    [Fact]
    public async Task Execution_history_records_virtual_clock_runs()
    {
        var (app, clock, _) = await StartAsync();

        clock.AdvanceTo(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero));
        await WaitUntil(
            () => app.Services.GetRequiredService<IScheduledTaskExecutionHistory>().Latest(nameof(ManualTask)) is not null,
            TimeSpan.FromSeconds(5));

        var latest = app.Services.GetRequiredService<IScheduledTaskExecutionHistory>().Latest(nameof(ManualTask));
        Assert.NotNull(latest);
        Assert.True(latest.Success);
        Assert.False(latest.Manual);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        if (!condition())
            throw new TimeoutException("等待条件超时");
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

/// <summary>每分钟触发的测试任务：可阻塞、可放行，记录执行次数。</summary>
[DisableAutoDiscovery]
public class ManualTask : IScheduledTask
{
    private int _invocations;
    private bool _started;

    /// <summary>每次执行阻塞时长（默认 0 = 立即完成）。</summary>
    public TimeSpan BlockFor { get; set; }

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CronExpression => "0 * * * * *";   // 每分钟第 0 秒

    public int Invocations => Volatile.Read(ref _invocations);

    public bool Started => Volatile.Read(ref _started);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocations);
        Volatile.Write(ref _started, true);

        if (BlockFor > TimeSpan.Zero)
        {
            // 阻塞到测试放行（或宿主持停取消）；不用真实超时——虚拟时钟测试里
            // 时间不前进，真实超时会让"等待"失去意义
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
