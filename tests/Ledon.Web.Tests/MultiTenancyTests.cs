using System.Net;
using System.Security.Claims;
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

    private async Task<HttpClient> StartAsync(
        Action<MultiTenancyOptions>? configureTenancy = null,
        Action<WebApplication>? configurePipeline = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonMultiTenancy(configureTenancy);

        var app = builder.Build();
        configurePipeline?.Invoke(app);
        app.UseLedonMultiTenancy();
        app.MapGet("/whoami", (ITenantContext tenant) => new
        {
            tenant.TenantId,
            IsMultiTenant = tenant.IsMultiTenant,
        });

        await app.StartAsync();
        _app = app;
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

    // ---------- 声明 / Host 子域名解析 ----------

    [Fact]
    public async Task Tenant_id_is_resolved_from_claim()
    {
        // claim 来源要求认证先于租户解析：生产由 UseAuthentication 完成，测试直接注入 User
        using var client = await StartAsync(
            o => o.Source = TenantSource.Claim,
            app => app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("tenant_id", "tenant-claim")], "test"));
                return next();
            }));

        var body = await client.GetStringAsync("/whoami");

        Assert.Contains("tenant-claim", body);
        Assert.Contains("true", body);
    }

    [Fact]
    public async Task Tenant_id_is_resolved_from_host_subdomain()
    {
        using var client = await StartAsync(o =>
        {
            o.Source = TenantSource.Host;
            o.HostSuffix = ".example.com";
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Host = "tenant-a.example.com";
        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("tenant-a", body);
        Assert.Contains("true", body);
    }

    [Fact]
    public async Task Apex_host_falls_back_to_single_tenant()
    {
        using var client = await StartAsync(o =>
        {
            o.Source = TenantSource.Host;
            o.HostSuffix = ".example.com";
        });

        // apex 域名（无子域名段）与无关域名都按单租户处理
        var apex = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        apex.Headers.Host = "example.com";
        var apexBody = await (await client.SendAsync(apex)).Content.ReadAsStringAsync();

        Assert.Contains("null", apexBody);
        Assert.Contains("false", apexBody);

        var foreign = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        foreign.Headers.Host = "tenant-b.other.com";
        var foreignBody = await (await client.SendAsync(foreign)).Content.ReadAsStringAsync();

        Assert.Contains("null", foreignBody);
        Assert.Contains("false", foreignBody);
    }

    [Fact]
    public async Task Missing_tenant_is_rejected_when_required()
    {
        using var client = await StartAsync(o => o.RequireTenant = true);

        // 漏带 X-Tenant-Id：400，而不是静默按单租户放行（那会让请求看到全量数据）
        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("missing_tenant", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_tenant_is_rejected_by_validator()
    {
        using var client = await StartAsync(o => o.TenantValidator = new AllowListValidator("tenant-a"));

        var ok = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        ok.Headers.Add("X-Tenant-Id", "tenant-a");
        var okResponse = await client.SendAsync(ok);
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);

        // 目录里没有的租户 ID：拒绝（否则客户端可以枚举租户）
        var bad = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        bad.Headers.Add("X-Tenant-Id", "tenant-zzz");
        var rejected = await client.SendAsync(bad);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("unknown_tenant", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Custom_status_code_for_missing_tenant()
    {
        using var client = await StartAsync(o =>
        {
            o.RequireTenant = true;
            o.MissingTenantStatusCode = 401;
        });

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class AllowListValidator(params string[] allowed) : ITenantValidator
    {
        public ValueTask<bool> IsValidAsync(string tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(allowed.Contains(tenantId, StringComparer.Ordinal));
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
