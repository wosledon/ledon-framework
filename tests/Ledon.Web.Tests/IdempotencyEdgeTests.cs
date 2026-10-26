using System.Net;
using Ledon.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>幂等模块边界：失败不缓存、长度/体积限制、租户分区、TTL 与响应头重放。</summary>
public class IdempotencyEdgeTests
{
    [Fact]
    public async Task Failed_response_is_not_cached_and_can_be_retried()
    {
        var hits = 0;
        await using var app = await StartAsync(async context =>
        {
            var current = Interlocked.Increment(ref hits);
            context.Response.StatusCode = current == 1 ? 500 : 200;
            await context.Response.WriteAsJsonAsync(new { hit = current });
        });

        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(client, "retry-key", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "retry-key", "{}")).StatusCode);
        Assert.Equal(2, hits);
    }

    [Fact]
    public async Task Invalid_key_and_oversized_body_are_rejected_before_endpoint()
    {
        var hits = 0;
        await using var app = await StartAsync(
            async context =>
            {
                Interlocked.Increment(ref hits);
                await context.Response.WriteAsJsonAsync(new { ok = true });
            },
            options =>
            {
                options.MaxKeyLength = 8;
                options.MaxRequestBodyBytes = 8;
            });

        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "too-long-key", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(client, "body-key", """{"value":"too large"}""")).StatusCode);
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task Same_key_is_isolated_by_tenant_partition()
    {
        var hits = 0;
        await using var app = await StartAsync(async context =>
        {
            Interlocked.Increment(ref hits);
            await context.Response.WriteAsJsonAsync(new { ok = true });
        });

        var client = app.GetTestClient();

        var first = await PostAsync(client, "shared-key", "{}", tenant: "tenant-a");
        var second = await PostAsync(client, "shared-key", "{}", tenant: "tenant-b");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(2, hits);
    }

    [Fact]
    public async Task Expired_record_allows_reexecution()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var hits = 0;
        await using var app = await StartAsync(
            async context =>
            {
                Interlocked.Increment(ref hits);
                await context.Response.WriteAsJsonAsync(new { ok = true });
            },
            options => options.Ttl = TimeSpan.FromMinutes(1),
            services => services.AddSingleton<TimeProvider>(time));

        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "ttl-key", "{}")).StatusCode);
        time.Now = time.Now.AddMinutes(2);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "ttl-key", "{}")).StatusCode);
        Assert.Equal(2, hits);
    }

    [Fact]
    public async Task Replay_restores_cached_location_header()
    {
        await using var app = await StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers.Location = "/orders/42";
            await context.Response.WriteAsJsonAsync(new { id = 42 });
        });

        var client = app.GetTestClient();

        var first = await PostAsync(client, "location-key", "{}");
        var replay = await PostAsync(client, "location-key", "{}");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("/orders/42", first.Headers.Location!.ToString());
        Assert.Equal("/orders/42", replay.Headers.Location!.ToString());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
    }

    private static async Task<WebApplication> StartAsync(
        RequestDelegate endpoint,
        Action<IdempotencyOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configureServices?.Invoke(builder.Services);
        builder.Services.AddLedonIdempotency(configure);

        var app = builder.Build();
        app.UseRouting();
        app.UseLedonIdempotency();
        app.MapPost("/orders", endpoint);

        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string key,
        string json,
        string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("Idempotency-Key", key);

        if (tenant is not null)
            request.Headers.Add("X-Tenant-Id", tenant);

        return await client.SendAsync(request);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
