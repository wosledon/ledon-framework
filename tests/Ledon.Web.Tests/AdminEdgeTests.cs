using System.Net;
using Ledon.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>运维端点边界：可选能力缺失、前缀规范化、无发件箱时的降级行为。</summary>
public class AdminEdgeTests
{
    [Fact]
    public void Invalid_route_prefix_is_normalized()
    {
        var services = new ServiceCollection();

        services.AddLedonAdmin(options => options.RoutePrefix = "ops/");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("/ops", provider.GetRequiredService<LedonAdminOptions>().RoutePrefix);
    }

    [Fact]
    public void Blank_route_prefix_is_rejected()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLedonAdmin(options => options.RoutePrefix = "   "));

        Assert.Contains("RoutePrefix", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_reports_nulls_without_optional_capabilities()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonAdmin();

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();

        var snapshot = await app.Services.GetRequiredService<OpsSnapshotService>().CaptureAsync();

        Assert.Null(snapshot.Outbox);
        Assert.Null(snapshot.Workflows);
        Assert.Null(snapshot.Scheduler);

        await app.StopAsync();
    }

    [Fact]
    public async Task Dead_letter_list_is_disabled_without_outbox_store()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonAdmin();

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/admin/ops/outbox/dead-letters");

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"enabled\":false", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await app.StopAsync();
    }

    [Fact]
    public async Task Replay_returns_404_without_outbox_store()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonAdmin();

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();

        var response = await app.GetTestClient()
            .PostAsync($"/admin/ops/outbox/dead-letters/{Guid.NewGuid()}/replay", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await app.StopAsync();
    }
}
