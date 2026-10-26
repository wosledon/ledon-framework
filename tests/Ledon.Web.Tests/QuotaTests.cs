using System.Net;
using Ledon.Quotas;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>配额：固定窗口计数、超限拒绝、窗口重置与中间件响应。</summary>
public class QuotaTests
{
    [Fact]
    public async Task Fixed_window_denies_and_resets()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new QuotaService(new InMemoryQuotaStore(time));

        var first = await service.ConsumeAsync("tenant-a", 2, TimeSpan.FromMinutes(1));
        var second = await service.ConsumeAsync("tenant-a", 2, TimeSpan.FromMinutes(1));
        var third = await service.ConsumeAsync("tenant-a", 2, TimeSpan.FromMinutes(1));

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(third.Allowed);
        Assert.Equal(0, third.Remaining);

        time.Now = time.Now.AddMinutes(2);

        var nextWindow = await service.ConsumeAsync("tenant-a", 2, TimeSpan.FromMinutes(1));
        Assert.True(nextWindow.Allowed);
    }

    [Fact]
    public async Task Middleware_returns_429_and_quota_headers()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonQuotas(o =>
        {
            o.DefaultLimit = 1;
            o.Window = TimeSpan.FromMinutes(1);
            o.KeySelector = _ => "fixed";
        });

        var app = builder.Build();
        app.UseLedonQuotas();
        app.MapGet("/ping", () => "pong");

        await app.StartAsync();
        await using var _ = app;

        var client = app.GetTestClient();

        var first = await client.GetAsync("/ping");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("0", first.Headers.GetValues("X-Quota-Remaining").Single());

        var second = await client.GetAsync("/ping");
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.True(second.Headers.Contains("Retry-After"));
        Assert.Contains("quota_exceeded", await second.Content.ReadAsStringAsync());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
