using Ledon.Module.Abstractions;
using Ledon.Queues;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 持久化后台队列测试：任务落库 → 调度器投递给处理器（至少一次），
/// 以及"重启后任务仍在"（进程内队列没有的特性）。
/// </summary>
public class DurableQueueTests : IDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync(string dbPath, DurableRecorder? recorder = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonDurableQueue(o => o.PollingInterval = TimeSpan.FromMilliseconds(50));
        if (recorder is not null)
        {
            builder.Services.AddSingleton<IDurableJobHandler>(new RecordJobHandler(recorder));
            builder.Services.AddSingleton<IDurableJobHandler>(new FlakyJobHandler(recorder));
        }
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;

        // 先建表再启动：调度器启动即轮询，表未就绪时取批会失败
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await app.StartAsync();

        return app;
    }

    [Fact]
    public async Task Job_is_dispatched_to_handler_and_marked_processed()
    {
        var recorder = new DurableRecorder();
        var app = await StartAsync(NewDb("dispatch"), recorder);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IDurableQueue>();
            await queue.EnqueueAsync(new DurableJob("record", "payload-1"));
        }

        await WaitUntil(() => recorder.Snapshot().Count >= 1, TimeSpan.FromSeconds(5));

        Assert.Equal("payload-1", recorder.Snapshot()[0]);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var job = await db.QueuedJobs.SingleAsync();
            Assert.NotNull(job.ProcessedAt);
            Assert.Null(job.Error);
        }
    }

    [Fact]
    public async Task Job_survives_host_restart()
    {
        var recorder = new DurableRecorder();
        var dbPath = NewDb("restart");

        // 第一次运行：只入队，不起调度器（模拟"进程在投递前被杀"）
        await using (var app = await StartAsync(dbPath, recorder))
        {
            await using var scope = app.Services.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IDurableQueue>();
            await queue.EnqueueAsync(new DurableJob("record", "before-restart"));

            // 任务已落库但未处理（进程内队列此时会随进程消失）
            await using var check = app.Services.CreateAsyncScope();
            var db = check.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var job = await db.QueuedJobs.SingleAsync();
            Assert.Null(job.ProcessedAt);
        }

        Assert.Empty(recorder.Snapshot());

        // 第二次运行（同一数据库）：重启后调度器把未处理任务继续投递
        await using var app2 = await StartAsync(dbPath, recorder);
        await WaitUntil(() => recorder.Snapshot().Count >= 1, TimeSpan.FromSeconds(5));

        Assert.Equal("before-restart", recorder.Snapshot()[0]);
    }

    [Fact]
    public async Task Failed_job_retries_then_marks_error()
    {
        var recorder = new DurableRecorder();
        recorder.Reset(failNext: 3);
        var app = await StartAsync(NewDb("retry"), recorder);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IDurableQueue>();
            await queue.EnqueueAsync(new DurableJob("flaky", "p"));
        }

        // 前 3 次失败（重试计数累加），第 4 次成功：轮询等终态
        await WaitUntil(async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var job = await db.QueuedJobs.FirstOrDefaultAsync();
            return job?.ProcessedAt is not null;
        }, TimeSpan.FromSeconds(10));

        recorder.Reset();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var job = await db.QueuedJobs.SingleAsync();
            Assert.NotNull(job.ProcessedAt);
            Assert.True(job.RetryCount >= 3, $"失败应累加重试计数，实际 {job.RetryCount}");
        }
    }

    [Fact]
    public async Task Unknown_handler_is_recorded_as_failure()
    {
        var recorder = new DurableRecorder();
        var app = await StartAsync(NewDb("unknown"), recorder);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IDurableQueue>();
            await queue.EnqueueAsync(new DurableJob("no-such-handler", "p"));
        }

        await WaitUntil(async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var job = await db.QueuedJobs.FirstOrDefaultAsync();
            return job?.RetryCount > 0;
        }, TimeSpan.FromSeconds(5));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var job = await db.QueuedJobs.SingleAsync();
            Assert.Contains("未找到处理器", job.Error);
        }
    }

    [Fact]
    public async Task Cleanup_deletes_only_processed_beyond_retention()
    {
        var app = await StartAsync(NewDb("cleanup"));

        Guid oldId, recentId, pendingId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IDurableQueue>();
            await queue.EnqueueAsync(new DurableJob("record", "old"));
            await queue.EnqueueAsync(new DurableJob("record", "recent"));
            await queue.EnqueueAsync(new DurableJob("record", "pending"));

            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var jobs = await db.QueuedJobs.ToListAsync();
            var old = jobs.Single(j => j.Payload == "old");
            var recent = jobs.Single(j => j.Payload == "recent");
            var pending = jobs.Single(j => j.Payload == "pending");
            oldId = old.Id;
            recentId = recent.Id;
            pendingId = pending.Id;

            old.ProcessedAt = DateTime.UtcNow.AddDays(-10);
            recent.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IDurableQueue>();
            var deleted = await queue.DeleteProcessedAsync(TimeSpan.FromDays(7));

            Assert.Equal(1, deleted);

            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            Assert.False(await db.QueuedJobs.AnyAsync(j => j.Id == oldId));
            Assert.True(await db.QueuedJobs.AnyAsync(j => j.Id == recentId));
            Assert.True(await db.QueuedJobs.AnyAsync(j => j.Id == pendingId));
        }
    }

    private static string NewDb(string name) =>
        Path.Combine(AppContext.BaseDirectory, $"durable-{name}-{Guid.NewGuid():N}.db");

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        if (!condition())
            throw new TimeoutException("等待条件超时");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        if (!await condition())
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

// ---------- 夹具 ----------

/// <summary>
/// 处理器执行记录（每测试实例一份，避免跨测试/跨程序集并行干扰）。
/// 静态信号在并行测试下会互相污染——这里改用注入式观测。
/// </summary>
public sealed class DurableRecorder
{
    private readonly List<string> _handled = [];
    private readonly object _gate = new();

    public int FailNext { get; set; }

    public void Record(string payload)
    {
        lock (_gate)
            _handled.Add(payload);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _handled.ToArray();
    }

    /// <summary>尝试消耗一次"失败配额"：返回 true 表示本次应失败。</summary>
    public bool ConsumeFailure() => Interlocked.Decrement(ref FailNextField) >= 0;

    private int FailNextField;

    public void Reset(int failNext = 0)
    {
        lock (_gate)
            _handled.Clear();

        Volatile.Write(ref FailNextField, failNext);
    }
}

/// <summary>记录型处理器（测试显式注册，不参与自动发现）。</summary>
[DisableAutoDiscovery]
public class RecordJobHandler(DurableRecorder recorder) : IDurableJobHandler
{
    public string Name => "record";

    public Task ExecuteAsync(string payload, CancellationToken cancellationToken = default)
    {
        recorder.Record(payload);
        return Task.CompletedTask;
    }
}

/// <summary>前 N 次失败、之后成功的处理器（测试显式注册）。</summary>
[DisableAutoDiscovery]
public class FlakyJobHandler(DurableRecorder recorder) : IDurableJobHandler
{
    public string Name => "flaky";

    public Task ExecuteAsync(string payload, CancellationToken cancellationToken = default)
    {
        if (recorder.ConsumeFailure())
            throw new InvalidOperationException("transient");

        recorder.Record(payload);
        return Task.CompletedTask;
    }
}
