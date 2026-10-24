using System.Net;
using System.Net.Http.Headers;
using Ledon.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>认证授权测试：JWT Bearer、API Key、权限策略三种通道。</summary>
public class SecurityTests : IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    private async Task<HttpClient> StartAsync(Action<LedonSecurityOptions>? configureSecurity = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonSecurity(configureSecurity);

        var app = builder.Build();
        app.UseLedonWeb();   // 认证/授权中间件由 UseLedonWeb 自动接线

        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    private static HttpRequestMessage WithBearer(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Jwt_bearer_authenticates_and_permission_policy_enforces()
    {
        var client = await StartAsync(options => options.ApiKey.Keys["test-key"] = "service-a");

        var tokens = _app!.Services.GetRequiredService<IJwtTokenGenerator>();
        var withPermission = tokens.Generate("u1", "alice", ["secure.read"]);
        var withoutPermission = tokens.Generate("u2", "bob");

        // 无令牌 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/secure/whoami")).StatusCode);

        // 有效 JWT → 200，身份正确
        var who = await client.SendAsync(WithBearer("/api/secure/whoami", withPermission));
        Assert.Equal(HttpStatusCode.OK, who.StatusCode);
        var body = await who.Content.ReadAsStringAsync();
        Assert.Contains("alice", body);
        Assert.Contains("secure.read", body);

        // 有权限 → 200；无权限 → 403
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(WithBearer("/api/secure/permission", withPermission))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.SendAsync(WithBearer("/api/secure/permission", withoutPermission))).StatusCode);
    }

    [Fact]
    public async Task Api_key_authenticates_without_token()
    {
        var client = await StartAsync(options => options.ApiKey.Keys["test-key"] = "service-a");

        // 默认策略接受 JWT 或 API Key 任一：API Key 直接放行
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/secure/whoami");
        request.Headers.Add("X-Api-Key", "test-key");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("service-a", await response.Content.ReadAsStringAsync());

        // 错误的 Key → 401
        var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/secure/whoami");
        wrong.Headers.Add("X-Api-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_gets_401_when_no_auth_configured_for_request()
    {
        // 配置了 API Key 但请求两者都没带 → 401（默认策略仍要求已认证用户）
        var client = await StartAsync(options => options.ApiKey.Keys["test-key"] = "service-a");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/secure/whoami")).StatusCode);
    }

    public void Dispose()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}
