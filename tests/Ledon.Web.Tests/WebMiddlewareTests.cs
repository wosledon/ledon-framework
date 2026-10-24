using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ledon.Security;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// Web 中间件测试：限流 / 响应压缩 / 请求超时 / 启动探针 / OpenAPI 安全方案。
/// 全部按需开关，默认关闭不改变既有行为。
/// </summary>
public class WebMiddlewareTests : IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    private async Task<HttpClient> StartAsync(
        Action<Ledon.Web.LedonWebOptions> configure,
        Action<Microsoft.AspNetCore.Builder.WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb(configure);

        var app = builder.Build();
        configureApp?.Invoke(app);
        app.UseLedonWeb();

        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    // ---------- 限流 ----------

    [Fact]
    public async Task Rate_limiting_rejects_burst_with_problem_details()
    {
        var client = await StartAsync(o =>
        {
            o.UseRateLimiting = true;
            o.RateLimitPermitLimit = 2;
            o.RateLimitWindowSeconds = 60;
        });

        // 前两次放行（同一分区：TestServer 无远端 IP）
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/hello")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/hello")).StatusCode);

        // 第三次超限：429 + ProblemDetails（RFC 7807）
        var rejected = await client.GetAsync("/hello");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("请求过于频繁", body);
        Assert.Contains("429", body);
    }

    [Fact]
    public async Task Rate_limiting_is_off_by_default()
    {
        var client = await StartAsync(_ => { });

        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/hello");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ---------- 响应压缩 ----------

    [Fact]
    public async Task Response_compression_compresses_when_client_accepts_gzip()
    {
        var client = await StartAsync(o => o.UseResponseCompression = true);

        var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.Content.Headers.ContentEncoding.Contains("gzip"),
            "响应未按 Accept-Encoding 压缩");
    }

    // ---------- 请求超时 ----------

    [Fact]
    public async Task Request_timeout_returns_504_for_slow_endpoint()
    {
        var client = await StartAsync(o =>
        {
            o.UseRequestTimeouts = true;
            o.RequestTimeoutSeconds = 1;
        });

        // 慢端点（5 秒）超过 1 秒超时：504 GatewayTimeout
        var response = await client.GetAsync("/slow");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    // ---------- 启动探针 ----------

    [Fact]
    public async Task Startup_probe_reports_200_after_host_started()
    {
        var client = await StartAsync(_ => { }, app => app.MapLedonStartup());

        var response = await client.GetAsync("/health/startup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- OpenAPI 安全方案 ----------

    [Fact]
    public async Task OpenApi_document_carries_bearer_scheme_and_title_when_security_registered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb(o =>
        {
            o.UseOpenApi = true;
            o.OpenApiTitle = "Ledon Test API";
        });
        builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = "unit-test-signing-key-please-change");

        var app = builder.Build();
        app.UseLedonWeb();

        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();

        var document = await _client.GetStringAsync("/openapi/v1.json");

        // 文档元数据 + Bearer 安全方案
        Assert.Contains("Ledon Test API", document);
        Assert.Contains("Bearer", document);
        Assert.Contains("bearer", document);

        // 受保护端点（[Authorize]）带安全要求
        Assert.Contains("security", document);
    }

    [Fact]
    public async Task OpenApi_document_has_no_security_scheme_without_authentication()
    {
        var client = await StartAsync(o =>
        {
            o.UseOpenApi = true;
            o.OpenApiTitle = "Ledon Plain API";
        });

        var document = await client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("Ledon Plain API", document);
        Assert.DoesNotContain("bearer", document);
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

// ---------- 夹具：慢端点（请求超时测试用） ----------

public class SlowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
        => builder.MapGet("/slow", async (CancellationToken cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return "slow-done";
        });
}
