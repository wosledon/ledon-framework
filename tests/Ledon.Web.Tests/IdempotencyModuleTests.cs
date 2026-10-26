using System.Net;
using Ledon.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>增强幂等模块：重放、请求指纹冲突、并发去重与缺省放行。</summary>
public class IdempotencyModuleTests
{
    [Fact]
    public async Task Same_key_and_body_replays_cached_response()
    {
        var hits = 0;
        await using var app = await StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await context.Response.WriteAsJsonAsync(new { id = "order-1" });
        });

        var client = app.GetTestClient();

        var first = await PostAsync(client, "key-1", """{"amount":10}""");
        var second = await PostAsync(client, "key-1", """{"amount":10}""");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, hits);
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Same_key_with_different_body_is_conflict()
    {
        var hits = 0;
        await using var app = await StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await context.Response.WriteAsJsonAsync(new { ok = true });
        });

        var client = app.GetTestClient();

        var first = await PostAsync(client, "key-2", """{"amount":10}""");
        var second = await PostAsync(client, "key-2", """{"amount":20}""");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Conflict").Single());
        Assert.Contains("idempotency_key_reused", await second.Content.ReadAsStringAsync());
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task Concurrent_same_key_executes_once()
    {
        var hits = 0;
        await using var app = await StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await Task.Delay(100);
            await context.Response.WriteAsJsonAsync(new { ok = true });
        });

        var client = app.GetTestClient();

        var responses = await Task.WhenAll(
            PostAsync(client, "key-3", """{"amount":10}"""),
            PostAsync(client, "key-3", """{"amount":10}"""));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, hits);
        Assert.Contains(responses, response => response.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task Request_without_key_is_not_affected()
    {
        var hits = 0;
        await using var app = await StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await context.Response.WriteAsJsonAsync(new { ok = true });
        });

        var client = app.GetTestClient();

        using var first = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = Json("""{"amount":1}""") };
        using var second = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = Json("""{"amount":1}""") };

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(second)).StatusCode);
        Assert.Equal(2, hits);
    }

    private static async Task<WebApplication> StartAsync(RequestDelegate endpoint)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonIdempotency(options => options.LockExpiry = TimeSpan.FromSeconds(10));

        var app = builder.Build();
        app.UseRouting();
        app.UseLedonIdempotency();
        app.MapPost("/orders", endpoint);

        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = Json(json) };
        request.Headers.Add("Idempotency-Key", key);

        return await client.SendAsync(request);
    }

    private static StringContent Json(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");
}
