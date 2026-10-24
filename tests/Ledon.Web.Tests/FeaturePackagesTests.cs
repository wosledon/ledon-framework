using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Ledon.Caching;
using Ledon.Data;
using Ledon.Module;
using Ledon.Observability;
using Ledon.Queues;
using Ledon.Scheduling;
using Ledon.Testing;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledon.Web.Tests;

/// <summary>功能包（缓存 / 后台队列 / 可观测性）测试。</summary>
public class FeaturePackagesTests : IDisposable
{
    private IHost? _host;
    private HttpClient? _client;

    private async Task<HttpClient> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        await app.StartAsync();
        _host = app;
        _client = app.GetTestClient();
        return _client;
    }

    // ---------- 缓存 ----------

    [Fact]
    public async Task Cache_get_or_create_uses_single_flight_for_concurrent_misses()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var calls = 0;
        Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.Delay(50, ct).ContinueWith(_ => "value", ct);
        }

        // 三个并发未命中必须只触发一次回源
        var results = await Task.WhenAll(
            cache.GetOrCreateAsync("k", Factory),
            cache.GetOrCreateAsync("k", Factory),
            cache.GetOrCreateAsync("k", Factory));

        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.Equal("value", result));

        Assert.True(cache.TryGet<string>("k", out var cached));
        Assert.Equal("value", cached);

        cache.Remove("k");
        Assert.False(cache.TryGet<string>("k", out _));
    }

    [Fact]
    public async Task Cache_single_flight_survives_one_callers_cancellation()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            // 遵守传入令牌：旧实现把第一个调用者的令牌绑到共享回源上，
            // 调用方 1 取消会导致这里整体取消，调用方 2 也跟着失败
            return release.Task.ContinueWith(_ => "value", ct);
        }

        using var cts = new CancellationTokenSource();

        // 调用方 1 发起回源并等待
        var caller1 = cache.GetOrCreateAsync("k", Factory, cancellationToken: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 调用方 1 取消：只应影响它自己，不能杀掉共享回源
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller1);

        // 调用方 2 仍能共享同一次回源并拿到结果
        var caller2 = cache.GetOrCreateAsync("k", Factory);
        release.TrySetResult();

        Assert.Equal("value", await caller2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Cache_key_prefix_isolates_instances()
    {
        var services = new ServiceCollection();
        services.AddLedonCaching(o => o.KeyPrefix = "a");
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        cache.Set("k", "from-a");

        // 不同前缀的另一个实例看不到对方的 key
        var other = new ServiceCollection();
        other.AddLedonCaching(o => o.KeyPrefix = "b");
        using var otherProvider = other.BuildServiceProvider();
        var otherCache = otherProvider.GetRequiredService<ICacheService>();

        Assert.True(cache.TryGet<string>("k", out var value));
        Assert.Equal("from-a", value);
        Assert.False(otherCache.TryGet<string>("k", out _));
    }

    // ---------- 后台队列 ----------

    [Fact]
    public async Task Background_queue_runs_items_in_scopes_and_isolates_failures()
    {
        var recorder = new Recorder();
        var client = await StartAsync(services =>
        {
            services.AddSingleton(recorder);
            services.AddLedonQueues();
        });

        var queue = _host!.Services.GetRequiredService<IBackgroundQueue>();

        // 任务 1：在独立作用域内解析服务
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.EnqueueAsync(async (scope, ct) =>
        {
            scope.GetRequiredService<Recorder>().Add("first");
            firstDone.TrySetResult();
            await Task.CompletedTask;
        });
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 任务 2：抛异常——不能拖垮 worker
        await queue.EnqueueAsync((scope, ct) => throw new InvalidOperationException("boom"));

        // 任务 3：失败之后仍能正常执行
        var thirdDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.EnqueueAsync((scope, ct) =>
        {
            scope.GetRequiredService<Recorder>().Add("third");
            thirdDone.TrySetResult();
            return Task.CompletedTask;
        });
        await thirdDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "first", "third" }, recorder.Snapshot());
    }

    // ---------- 可观测性 ----------

    [Fact]
    public async Task Health_checks_are_auto_discovered_and_aggregated()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonHealthChecks();

        var app = builder.Build();
        app.MapLedonHealthChecks();
        await app.StartAsync();

        // 两个检查（一个健康、一个故障）都被自动发现：聚合结果为 Unhealthy。
        // 若 BrokenHealthCheck 没被发现，这里会是 200——聚合结果反过来证明发现生效。
        var response = await app.GetTestClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());

        await app.StopAsync();
    }

    [Fact]
    public async Task Health_liveness_and_readiness_are_separated()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonHealthChecks();

        var app = builder.Build();
        app.MapLedonLiveness();
        app.MapLedonReadiness();
        await app.StartAsync();
        var client = app.GetTestClient();

        // 存活：进程在跑即 200（有故障检查也不影响——用于重启判断）
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        // 就绪：依赖检查不过则 503（BrokenHealthCheck 被自动发现）——用于摘流判断
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        await app.StopAsync();
    }

    // ---------- 依赖健康检查 ----------

    [Fact]
    public async Task Database_health_check_is_registered_by_data_layer()
    {
        // AddLedonData 自动登记 database 检查；DbInitializer 已建库 → 条目 Healthy
        // （BrokenHealthCheck 会让整体 Unhealthy，但 database 条目本身必须健康——直查 report）
        TodoDbPath.Value = Path.Combine(AppContext.BaseDirectory, $"ledon-health-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonData();
        builder.Services.AddLedonHealthChecks();

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();

        var report = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var database = report.Entries.FirstOrDefault(entry => entry.Key == "database");
        Assert.NotEqual(default, database);
        Assert.Equal(HealthStatus.Healthy, database.Value.Status);

        await app.StopAsync();
    }

    [Fact]
    public async Task Redis_health_check_is_registered_when_connection_string_configured()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonHealthChecks(o => o.RedisConnectionString = "localhost:6379");

        var app = builder.Build();
        await app.StartAsync();

        // 只验证登记（不真连 Redis：连接在检查执行时才建立）
        var registrations = app.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.Contains(registrations, registration => registration.Name == "redis");

        await app.StopAsync();
    }

    [Fact]
    public async Task Observability_registration_does_not_block_host_lifecycle()
    {
        // 无 OTLP 端点（环境变量驱动）：注册与宿主启停不应抛任何异常
        var client = await StartAsync(services =>
            services.AddLedonObservability(o => o.ServiceName = "ledon-tests"));

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // 无端点，仅验证管线可用
    }

    [Fact]
    public async Task Observability_logs_export_does_not_block_host_lifecycle()
    {
        // 开启日志导出（含控制台导出器）：宿主启停与日志写入不应抛任何异常
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonObservability(o =>
        {
            o.ServiceName = "ledon-tests";
            o.UseConsoleExporter = true;   // span/指标/日志全部走控制台
        });

        var app = builder.Build();
        await app.StartAsync();

        var logger = app.Services.GetRequiredService<ILogger<FeaturePackagesTests>>();
        logger.LogInformation("可观测性日志导出冒烟（含作用域）");
        using (logger.BeginScope("scope-{Id}", Guid.NewGuid()))
        {
            logger.LogWarning("带作用域的日志");
        }

        await app.StopAsync();
    }

    [Fact]
    public async Task Prometheus_scraping_endpoint_serves_metrics()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonObservability(o =>
        {
            o.ServiceName = "ledon-tests";
            o.UsePrometheusExporter = true;
        });

        var app = builder.Build();
        app.MapLedonPrometheus();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        await app.StopAsync();
    }

    // ---------- 程序集扫描（允许列表） ----------

    [Fact]
    public void Scan_set_excludes_framework_and_infrastructure_assemblies()
    {
        var names = LedonAssemblies.Scan()
            .Select(assembly => assembly.GetName().Name!)
            .ToHashSet(StringComparer.Ordinal);

        // 引用框架的测试程序集被纳入
        Assert.Contains(typeof(FeaturePackagesTests).Assembly.GetName().Name!, names);

        // 框架功能包自身不扫（即使测试项目直接引用它们）
        Assert.DoesNotContain(typeof(Ledon.Web.IEndpoint).Assembly.GetName().Name!, names);
        Assert.DoesNotContain(typeof(Ledon.Data.ILedonDbContext).Assembly.GetName().Name!, names);
        Assert.DoesNotContain(typeof(Ledon.Caching.ICacheService).Assembly.GetName().Name!, names);
        Assert.DoesNotContain(typeof(Ledon.Queues.IBackgroundQueue).Assembly.GetName().Name!, names);
        Assert.DoesNotContain(typeof(Ledon.Observability.LedonObservabilityOptions).Assembly.GetName().Name!, names);

        // EF Core 等基础设施不扫
        Assert.DoesNotContain(typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName().Name!, names);
    }

    // ---------- 分布式缓存 ----------

    [Fact]
    public async Task Distributed_cache_shares_values_across_instances()
    {
        // 同一 IDistributedCache 上的两个 ICacheService 实例 = 两个应用实例共享缓存
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddLedonDistributedCaching(o => o.KeyPrefix = "test:");
        using var provider = services.BuildServiceProvider();

        var cacheA = provider.GetRequiredService<ICacheService>();
        var cacheB = provider.GetRequiredService<ICacheService>();

        cacheA.Set("k", new Payload { Name = "from-a" });

        // 实例 B 立即可见（多实例共享语义）
        Assert.True(cacheB.TryGet<Payload>("k", out var seen));
        Assert.Equal("from-a", seen!.Name);

        // 前缀隔离：另一个前缀的实例看不到
        var other = new ServiceCollection();
        other.AddDistributedMemoryCache();
        other.AddLedonDistributedCaching(o => o.KeyPrefix = "other:");
        using var otherProvider = other.BuildServiceProvider();
        Assert.False(otherProvider.GetRequiredService<ICacheService>().TryGet<Payload>("k", out _));

        // 回源后写入
        var created = await cacheA.GetOrCreateAsync("new", _ => Task.FromResult(new Payload { Name = "created" }));
        Assert.Equal("created", created!.Name);
        Assert.True(cacheB.TryGet<Payload>("new", out var shared));
        Assert.Equal("created", shared!.Name);
    }

    [Fact]
    public async Task Distributed_cache_round_trips_against_real_redis()
    {
        // 真实 Redis（Testcontainers）——需要 Docker；无 Docker 环境跳过
        if (!await DockerAvailableAsync())
            return;

        await using var redis = new LedonRedisContainer();
        var connectionString = await redis.StartAsync();

        var services = new ServiceCollection();
        services.AddLedonDistributedCaching(o => o.RedisConnectionString = connectionString);
        using var provider = services.BuildServiceProvider();

        var cacheA = provider.GetRequiredService<ICacheService>();
        var cacheB = provider.GetRequiredService<ICacheService>();

        cacheA.Set("k", new Payload { Name = "from-redis" });
        Assert.True(cacheB.TryGet<Payload>("k", out var seen));
        Assert.Equal("from-redis", seen!.Name);
    }

    private static async Task<bool> DockerAvailableAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 定时任务 ----------

    [Fact]
    public async Task Scheduled_task_fires_on_cron_schedule()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonScheduling();

        var app = builder.Build();
        await app.StartAsync();

        // EverySecondTask 被自动发现并注册，宿主启动后按 cron 每秒触发
        var task = app.Services.GetServices<IScheduledTask>().OfType<EverySecondTask>().Single();

        await task.Fired.WaitAsync(TimeSpan.FromSeconds(5));

        await app.StopAsync();
    }

    [Fact]
    public async Task Scheduled_task_drops_triggers_missed_during_execution_by_default()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonScheduling();   // 默认 CatchUpMissedTriggers = false

        var app = builder.Build();
        await app.StartAsync();

        var task = app.Services.GetServices<IScheduledTask>().OfType<SlowTask>().Single();

        // 首次执行开始并阻塞 6s（4s 周期的 cron，期间跨过一个触发点）
        await task.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(6000);
        Assert.Equal(1, task.Invocations);

        // 放行：错过的触发点被丢弃，下一次执行要等到"执行完毕后"的下一个未来触发点（约 2s 后），
        // 不会立即补跑——1.5s 内不应出现第二次执行
        task.Release.TrySetResult();
        await Assert.ThrowsAsync<TimeoutException>(
            () => task.SecondRun.WaitAsync(TimeSpan.FromSeconds(1.5)));

        // 下一个未来触发点到达后恢复执行
        await task.SecondRun.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(task.Invocations >= 2);

        await app.StopAsync();
    }

    [Fact]
    public async Task Scheduled_task_catches_up_missed_triggers_when_enabled()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonScheduling(o => o.CatchUpMissedTriggers = true);

        var app = builder.Build();
        await app.StartAsync();

        var task = app.Services.GetServices<IScheduledTask>().OfType<SlowTask>().Single();

        await task.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(6000);
        Assert.Equal(1, task.Invocations);

        // 放行：错过的触发点立即补上（无需等到下一个未来触发点）
        task.Release.TrySetResult();
        await task.SecondRun.WaitAsync(TimeSpan.FromSeconds(1.5));
        Assert.True(task.Invocations >= 2);

        await app.StopAsync();
    }

    // ---------- 领导权选举（多实例部署） ----------

    [Fact]
    public async Task Scheduled_task_does_not_run_without_leadership()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // 领导权被其它实例持有：本实例不执行任何定时任务
        builder.Services.AddSingleton<ISchedulerLock>(new FakeSchedulerLock(granted: false));
        builder.Services.AddLedonScheduling();

        var app = builder.Build();
        await app.StartAsync();

        var task = app.Services.GetServices<IScheduledTask>().OfType<EverySecondTask>().Single();

        // 非领导实例：2 秒内不可能触发（正常持有领导权时 1 秒内即触发）
        await Assert.ThrowsAsync<TimeoutException>(
            () => task.Fired.WaitAsync(TimeSpan.FromSeconds(2)));

        await app.StopAsync();
    }

    [Fact]
    public async Task Scheduled_task_runs_with_leadership()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISchedulerLock>(new FakeSchedulerLock(granted: true));
        builder.Services.AddLedonScheduling();

        var app = builder.Build();
        await app.StartAsync();

        var task = app.Services.GetServices<IScheduledTask>().OfType<EverySecondTask>().Single();

        // 持有领导权：按 cron 正常触发
        await task.Fired.WaitAsync(TimeSpan.FromSeconds(5));

        await app.StopAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();

        if (_host is not null)
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
