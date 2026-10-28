using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>Aspire 默认值边界：空参数、失败检查的就绪语义。</summary>
public class AspireEdgeTests
{
    [Fact]
    public void Null_builder_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => LedonServiceDefaultsExtensions.AddLedonServiceDefaults(null!));
    }

    [Fact]
    public void Null_application_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => LedonServiceDefaultsExtensions.MapLedonDefaultEndpoints(null!));
    }

    [Fact]
    public async Task Readiness_reports_failing_health_check()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonServiceDefaults();

        var app = builder.Build();
        app.MapLedonDefaultEndpoints();
        await app.StartAsync();

        // 测试程序集里的 BrokenHealthCheck 会被自动发现：存活仍 200，就绪 503。
        var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        await app.StopAsync();
    }
}
