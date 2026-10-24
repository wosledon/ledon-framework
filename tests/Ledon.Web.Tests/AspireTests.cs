using Ledon.Observability;
using Ledon.Resilience;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;

namespace Ledon.Web.Tests;

/// <summary>Aspire 服务默认值测试：一行装配 + 默认端点。</summary>
public class AspireTests : IDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Service_defaults_wire_capabilities_and_endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonServiceDefaults();

        var app = builder.Build();
        app.MapLedonDefaultEndpoints();
        _app = app;
        await app.StartAsync();

        // 默认端点：存活（就绪会被测试程序集里的 BrokenHealthCheck 拖成 503，属预期）
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync("/health/live")).StatusCode);

        // 各能力已在容器里
        Assert.NotNull(app.Services.GetRequiredService<IOptions<LedonResilienceOptions>>());
        Assert.NotNull(app.Services.GetRequiredService<IOptions<LedonObservabilityOptions>>());
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
