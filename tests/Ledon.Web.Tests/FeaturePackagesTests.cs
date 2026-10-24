using System.Net;
using System.Net.Http.Json;
using Ledon.Caching;
using Ledon.Module;
using Ledon.Observability;
using Ledon.Queues;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    [Fact]
    public async Task Observability_registration_does_not_block_host_lifecycle()
    {
        // 无 OTLP 端点（环境变量驱动）：注册与宿主启停不应抛任何异常
        var client = await StartAsync(services =>
            services.AddLedonObservability(o => o.ServiceName = "ledon-tests"));

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // 无端点，仅验证管线可用
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
