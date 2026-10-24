using System.Net;
using Ledon.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>多租户测试：请求头解析、单租户回退、后台任务环境租户穿透。</summary>
public class MultiTenancyTests : IDisposable
{
    private WebApplication? _app;

    private static async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonMultiTenancy();

        var app = builder.Build();
        app.UseLedonMultiTenancy();
        app.MapGet("/whoami", (ITenantContext tenant) => new
        {
            tenant.TenantId,
            IsMultiTenant = tenant.IsMultiTenant,
        });

        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Tenant_id_is_resolved_from_header()
    {
        using var client = await StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Add("X-Tenant-Id", "tenant-a");
        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("tenant-a", body);
        Assert.Contains("true", body);
    }

    [Fact]
    public async Task Missing_tenant_header_falls_back_to_single_tenant()
    {
        using var client = await StartAsync();

        var body = await client.GetStringAsync("/whoami");

        Assert.Contains("null", body);
        Assert.Contains("false", body);
    }

    [Fact]
    public void Ambient_tenant_flows_into_background_work()
    {
        // 后台任务：入队时捕获环境租户，执行期间可见（Web 请求外不下文也不丢）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonMultiTenancy();
        using var provider = services.BuildServiceProvider();

        string? seenInside = null;
        using (TenantContext.SetAmbient("tenant-bg"))
        {
            seenInside = provider.GetRequiredService<ITenantContext>().TenantId;
        }

        Assert.Equal("tenant-bg", seenInside);

        using (TenantContext.SetAmbient(null))
        {
            Assert.Null(provider.GetRequiredService<ITenantContext>().TenantId);
        }
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
