using System.Net;
using Ledon.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>可观测性边界：探针过滤、自定义路径、导出器开关。</summary>
public class ObservabilityEdgeTests
{
    [Fact]
    public async Task Prometheus_custom_path_is_honored()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonObservability(options =>
        {
            options.ServiceName = "ledon-edge-tests";
            options.UsePrometheusExporter = true;
        });

        var app = builder.Build();
        app.MapLedonPrometheus("/custom-metrics");
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/custom-metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        await app.StopAsync();
    }

    [Fact]
    public void Prometheus_mapping_rejects_null_endpoints()
    {
        Assert.Throws<ArgumentNullException>(() => LedonPrometheusEndpointExtensions.MapLedonPrometheus(null!));
    }

    [Fact]
    public async Task Liveness_with_failing_check_still_returns_healthy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonHealthChecks();

        var app = builder.Build();
        app.MapLedonLiveness();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());

        await app.StopAsync();
    }

    [Fact]
    public async Task Custom_health_path_excludes_failing_checks_via_predicate()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonHealthChecks();

        var app = builder.Build();
        app.MapLedonHealthChecks("/custom-health", _ => false);
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/custom-health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());

        await app.StopAsync();
    }

    [Fact]
    public async Task Observability_with_disabled_exporters_does_not_block_lifecycle()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonObservability(options =>
        {
            options.EnableTracing = false;
            options.EnableMetrics = false;
            options.EnableLogs = false;
        });

        var app = builder.Build();
        await app.StartAsync();
        await app.StopAsync();
    }
}
