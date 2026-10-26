using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

        // 显式指定签名密钥：框架会在启动时拒绝默认密钥（生产不能拿公开密钥签发令牌）
        builder.Services.AddLedonSecurity(options =>
        {
            options.Jwt.SigningKey = TestSigningKey;
            configureSecurity?.Invoke(options);
        });

        var app = builder.Build();
        app.UseLedonWeb();   // 认证/授权中间件由 UseLedonWeb 自动接线

        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    private const string TestSigningKey = "test-signing-key-at-least-32-characters-long!!";

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Default_signing_key_is_rejected_at_registration()
    {
        var services = new ServiceCollection();

        // 默认密钥是公开写在仓库里的：生产忘记覆盖 = 任何人都能伪造登录令牌，
        // 必须在注册阶段就失败（早失败比带着隐患启动好）
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddLedonSecurity());
        Assert.Contains("SigningKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_signing_key_is_rejected_at_registration()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddLedonSecurity(o => o.Jwt.SigningKey = "too-short"));

        Assert.Contains("32", ex.Message, StringComparison.Ordinal);
    }

    private static HttpRequestMessage WithBearer(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Expired_refresh_tokens_are_evicted_from_store()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLedonSecurity(o => o.Jwt.SigningKey = TestSigningKey);

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IRefreshTokenStore>();

        await store.StoreAsync("u1", "expired", time.Now.AddMinutes(5));

        // 时间推进到过期之后，再写入一条触发全量清理（至少每分钟一次）
        time.Now = time.Now.AddMinutes(10);
        await store.StoreAsync("u2", "fresh", time.Now.AddMinutes(5));

        Assert.Null(await store.FindAsync("expired"));
        Assert.NotNull(await store.FindAsync("fresh"));
    }

    [Fact]
    public async Task Refresh_service_treats_missing_token_as_invalid()
    {
        var services = new ServiceCollection();
        services.AddLedonSecurity(o => o.Jwt.SigningKey = TestSigningKey);

        await using var provider = services.BuildServiceProvider();
        var refresh = provider.GetRequiredService<IRefreshTokenService>();

        // JSON 里的 refreshToken: null / "" 不应变成 500
        Assert.Null(await refresh.ValidateAsync(null!));
        Assert.Null(await refresh.RotateAsync(""));
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

    // ---------- 刷新令牌 ----------

    [Fact]
    public async Task Refresh_token_round_trips_and_rotation_invalidates_old()
    {
        await using var provider = new ServiceCollection()
            .AddLedonSecurity(o => o.Jwt.SigningKey = TestSigningKey)
            .BuildServiceProvider();

        var refresh = provider.GetRequiredService<IRefreshTokenService>();

        var token = await refresh.IssueAsync("u1");
        Assert.Equal("u1", await refresh.ValidateAsync(token));

        // 轮换：新令牌可用，旧令牌立即作废
        var rotated = await refresh.RotateAsync(token);
        Assert.NotNull(rotated);
        Assert.NotEqual(token, rotated);
        Assert.Equal("u1", await refresh.ValidateAsync(rotated!));
        Assert.Null(await refresh.ValidateAsync(token));

        // 未知/篡改的令牌 → null
        Assert.Null(await refresh.ValidateAsync("not-a-real-token"));
        Assert.Null(await refresh.RotateAsync("not-a-real-token"));
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        await using var provider = new ServiceCollection()
            .AddLedonSecurity(o =>
{
    o.Jwt.SigningKey = TestSigningKey;
    o.RefreshTokenExpiration = TimeSpan.FromMilliseconds(1);
})
            .BuildServiceProvider();

        var refresh = provider.GetRequiredService<IRefreshTokenService>();

        var token = await refresh.IssueAsync("u1");
        await Task.Delay(50);

        Assert.Null(await refresh.ValidateAsync(token));
        Assert.Null(await refresh.RotateAsync(token));
    }

    [Fact]
    public async Task Replayed_revoked_token_revokes_the_whole_family()
    {
        await using var provider = new ServiceCollection()
            .AddLedonSecurity(o => o.Jwt.SigningKey = TestSigningKey)
            .BuildServiceProvider();

        var refresh = provider.GetRequiredService<IRefreshTokenService>();

        var first = await refresh.IssueAsync("u1");
        var second = await refresh.RotateAsync(first);
        Assert.NotNull(second);

        // 重放已吊销的 first：不仅拒绝，还把该用户其余令牌全部作废（second 也失效）
        Assert.Null(await refresh.ValidateAsync(first));
        Assert.Null(await refresh.ValidateAsync(second));
    }

    // ---------- 认证端点（登录/刷新） ----------

    [Fact]
    public async Task Auth_endpoints_issue_and_rotate_tokens()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = TestSigningKey);   // TestUserValidator 自动发现

        var app = builder.Build();
        app.UseLedonWeb();
        app.MapLedonAuthEndpoints();
        _app = app;
        await app.StartAsync();
        var client = app.GetTestClient();

        // 登录：双令牌（访问 + 刷新）
        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("alice", "password"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));

        // 访问令牌直接可用
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(WithBearer("/api/secure/whoami", tokens.AccessToken))).StatusCode);

        // 错误密码 → 401
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/auth/login", new LoginRequest("alice", "wrong"))).StatusCode);

        // 刷新：换到新双令牌，旧刷新令牌随即作废
        var refreshed = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var rotated = await refreshed.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(rotated);
        Assert.NotEqual(tokens.RefreshToken, rotated.RefreshToken);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(tokens.RefreshToken))).StatusCode);

        // 新访问令牌同样可用
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(WithBearer("/api/secure/whoami", rotated.AccessToken))).StatusCode);
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
