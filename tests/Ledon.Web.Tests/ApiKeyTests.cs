using System.Net;
using Ledon.Security.ApiKeys;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>动态 API Key：签发/校验/过期/吊销/列表与认证处理器。</summary>
public class ApiKeyTests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Keys_are_hashed_and_validated()
    {
        var store = new InMemoryApiKeyStore();
        var service = new ApiKeyService(store, new ApiKeyManagementOptions { Prefix = "led" });

        var created = await service.CreateAsync(
            "ci",
            scopes: ["orders.read"],
            tenantId: "tenant-a",
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.StartsWith("led_", created.Plaintext);
        Assert.NotEqual(created.Plaintext, created.Key.KeyHash);
        Assert.Equal(ApiKeyService.Hash(created.Plaintext), created.Key.KeyHash);

        var validated = await service.ValidateAsync(created.Plaintext);
        Assert.NotNull(validated);
        Assert.Equal("ci", validated.Name);
        Assert.Equal("tenant-a", validated.TenantId);
        Assert.Contains("orders.read", validated.Scopes);
        Assert.NotNull((await service.ListAsync("tenant-a")).Single().LastUsedAtUtc);

        Assert.Null(await service.ValidateAsync("wrong"));

        Assert.True(await service.RevokeAsync(created.Key.Id));
        Assert.Null(await service.ValidateAsync(created.Plaintext));
    }

    [Fact]
    public async Task Expired_keys_are_rejected()
    {
        var service = new ApiKeyService(new InMemoryApiKeyStore(), new ApiKeyManagementOptions());
        var created = await service.CreateAsync("expired", expiresAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.Null(await service.ValidateAsync(created.Plaintext));
    }

    [Fact]
    public async Task Authentication_handler_builds_principal_with_scopes_and_tenant()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonApiKeys(o => o.HeaderName = "X-Api-Key");
        builder.Services.AddAuthentication(ApiKeyDefaults.Scheme);

        var app = builder.Build();
        app.UseLedonWeb();
        app.MapGet("/secure", (HttpContext context) => new
        {
            user = context.User.Identity?.Name,
            tenant = context.User.FindFirst("tenant")?.Value,
            canRead = context.User.HasClaim("permission", "orders.read"),
        }).RequireAuthorization();

        await app.StartAsync();
        _app = app;

        var service = app.Services.GetRequiredService<ApiKeyService>();
        var created = await service.CreateAsync("web", scopes: ["orders.read"], tenantId: "tenant-a");

        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/secure");
        request.Headers.Add("X-Api-Key", created.Plaintext);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"user\":\"web\"", body);
        Assert.Contains("\"tenant\":\"tenant-a\"", body);
        Assert.Contains("\"canRead\":true", body);

        var anonymous = await client.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
