using Ledon.Queues;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 后台队列停机语义：默认开启有界 drain——宿主停止时队列里已入队未处理的任务
/// 会被继续处理完（正在执行中的任务仍由停机令牌取消，语义不变）。
/// </summary>
public class QueueShutdownTests : IDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Shutdown_drains_queued_items()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonQueues(o => o.DrainTimeout = TimeSpan.FromSeconds(10));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        var queue = app.Services.GetRequiredService<IBackgroundQueue>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 第一个任务占住 worker（阻塞在闸门上），第二、三个排在队列里
        await queue.EnqueueAsync(async (_, ct) =>
        {
            try { await gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { /* 停机取消：预期 */ }
        });
        await queue.EnqueueAsync(async (_, _) => secondRan.TrySetResult());
        await queue.EnqueueAsync(async (_, _) => thirdRan.TrySetResult());

        // 等第一个任务真正开始执行
        await Task.Delay(200);

        // 停止宿主：在途任务被取消，排队任务进入 drain
        await app.StopAsync();

        // drain 把排队任务处理完（有界 10s）
        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await thirdRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Drain_disabled_drops_pending_items()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonQueues(o => o.DrainTimeout = TimeSpan.Zero);

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        var queue = app.Services.GetRequiredService<IBackgroundQueue>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRan = false;

        await queue.EnqueueAsync(async (_, ct) =>
        {
            try { await gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { /* 停机取消：预期 */ }
        });
        await queue.EnqueueAsync(async (_, _) => pendingRan = true);

        await Task.Delay(200);
        await app.StopAsync();

        // 不 drain：排队任务被丢弃（不执行）
        Assert.False(pendingRan);
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
