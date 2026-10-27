using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ledon.Security;
using Ledon.Security.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>内置 RBAC 端到端：JWT 登录、admin 引导、管理端点权限隔离。</summary>
public class RbacEndToEndTests
{
    [Fact]
    public async Task Bootstrap_admin_can_manage_roles_while_normal_user_is_forbidden()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var adminToken = await LoginAsync(client, "admin", "admin123");
        var userToken = await LoginAsync(client, "user", "user123");

        var adminRoles = await client.SendAsync(Authorized(HttpMethod.Get, "/auth/authorization/roles", adminToken));
        Assert.Equal(HttpStatusCode.OK, adminRoles.StatusCode);

        var userRoles = await client.SendAsync(Authorized(HttpMethod.Get, "/auth/authorization/roles", userToken));
        Assert.Equal(HttpStatusCode.Forbidden, userRoles.StatusCode);

        var userMe = await client.SendAsync(Authorized(HttpMethod.Get, "/auth/authorization/me", userToken));
        Assert.Equal(HttpStatusCode.OK, userMe.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_rejected_on_management_endpoint()
    {
        await using var app = await StartAsync();
        var response = await app.GetTestClient().GetAsync("/auth/authorization/roles");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string> LoginAsync(HttpClient client, string userName, string password)
    {
        using var response = await client.PostAsJsonAsync("/auth/login", new { userName, password });
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return token!.AccessToken;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<ILedonUserValidator, TestUserValidator>();

        builder.AddLedonWeb();
        builder.Services.AddLedonSecurity(options =>
            options.Jwt.SigningKey = "rbac-e2e-test-signing-key-0123456789");
        builder.Services.AddLedonAuthorization(options =>
            options.BootstrapAdminUserIds.Add("admin"));

        var app = builder.Build();
        app.UseLedonWeb();
        app.MapLedonAuthEndpoints();

        await app.StartAsync();
        return app;
    }

    public sealed class TestUserValidator : ILedonUserValidator
    {
        public Task<LedonUser?> ValidateAsync(string userName, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult<LedonUser?>(userName switch
            {
                "admin" when password == "admin123" => new LedonUser("admin", "admin", []),
                "user" when password == "user123" => new LedonUser("user", "user", []),
                _ => null,
            });

        public Task<LedonUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LedonUser?>(userId switch
            {
                "admin" => new LedonUser("admin", "admin", []),
                "user" => new LedonUser("user", "user", []),
                _ => null,
            });
    }
}
