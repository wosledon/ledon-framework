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
