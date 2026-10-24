using System.Net;
using System.Net.Http.Json;
using Ledon.Caching;
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
    public async Task Health_checks_are_auto_discovered_and_served()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonHealthChecks();

        var app = builder.Build();
        app.MapLedonHealthChecks();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());

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
