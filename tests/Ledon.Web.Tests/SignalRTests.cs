using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Ledon.Web.Tests;

/// <summary>SignalR 集成测试：Hub 自动发现与约定路径映射。</summary>
public class SignalRTests : IDisposable
{
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonSignalR();   // ChatHub 自动发现

        var app = builder.Build();
        app.MapLedonSignalRHubs();
        _app = app;
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Hub_is_auto_discovered_and_mapped_by_convention()
    {
        var client = await StartAsync();

        // ChatHub → /hubs/chat：协商端点应答 200（连接信息）
        var negotiate = await client.PostAsync("/hubs/chat/negotiate", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);

        // 未发现的路径 → 404
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/hubs/nope/negotiate", null)).StatusCode);
    }

    [Fact]
    public async Task Mapping_is_idempotent_for_repeated_calls()
    {
        var client = await StartAsync();

        var app = _app!;
        app.MapLedonSignalRHubs();   // 重复映射同路径：SignalR 内部按路径去重，不炸

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/hubs/chat/negotiate", null)).StatusCode);
    }

    [Fact]
    public void Redis_backplane_is_wired_when_connection_string_configured()
    {
        var services = new ServiceCollection();
        services.AddLedonSignalR(o => o.RedisConnectionString = "localhost:6399");

        // backplane 把 Hub 生命周期管理器替换为 Redis 实现（注册级断言：实例化依赖 hub 上下文）
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(HubLifetimeManager<>)
            && descriptor.ImplementationType?.Name.Contains("Redis", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Default_lifetime_manager_without_redis()
    {
        var services = new ServiceCollection();
        services.AddLedonSignalR();

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(HubLifetimeManager<>)
            && descriptor.ImplementationType?.Name.Contains("Redis", StringComparison.Ordinal) == true);
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
