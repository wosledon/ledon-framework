using System.Net;
using System.Text.Json;
using Ledon.Web.ApiConventions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>全局异常处理边界：内置状态码映射、自定义映射优先级、5xx 不泄露细节。</summary>
public class WebExceptionEdgeTests
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseLedonWeb();

        app.MapGet("/throw/argument", () => { throw new ArgumentException("bad input"); });
        app.MapGet("/throw/missing", () => { throw new KeyNotFoundException("order-42"); });
        app.MapGet("/throw/forbidden", () => { throw new UnauthorizedAccessException("nope"); });
        app.MapGet("/throw/internal", () => { throw new InvalidOperationException("secret connection string"); });
        app.MapGet("/throw/validation", () => { throw new ValidationFailure(); });

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task Argument_exception_maps_to_400_with_detail()
    {
        var (app, client) = await StartAsync();

        var response = await client.GetAsync("/throw/argument");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("bad input", json.RootElement.GetProperty("detail").GetString());

        await app.StopAsync();
    }

    [Fact]
    public async Task Missing_resource_maps_to_404()
    {
        var (app, client) = await StartAsync();

        var response = await client.GetAsync("/throw/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task Unauthorized_maps_to_403()
    {
        var (app, client) = await StartAsync();

        var response = await client.GetAsync("/throw/forbidden");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task Custom_mapping_takes_precedence_over_builtin_rules()
    {
        var (app, client) = await StartAsync(services =>
            services.AddLedonExceptionMapping(new TeapotMapping()));

        var response = await client.GetAsync("/throw/internal");

        Assert.Equal((HttpStatusCode)418, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("teapot", json.RootElement.GetProperty("title").GetString());

        await app.StopAsync();
    }

    [Fact]
    public async Task Internal_error_hides_exception_detail()
    {
        var (app, client) = await StartAsync();

        var response = await client.GetAsync("/throw/internal");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret connection string", body, StringComparison.Ordinal);
        Assert.Contains("correlationId", body, StringComparison.Ordinal);

        await app.StopAsync();
    }

    [Fact]
    public async Task Validation_errors_are_returned_as_extension()
    {
        var (app, client) = await StartAsync();

        var response = await client.GetAsync("/throw/validation");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"errors\"", body, StringComparison.Ordinal);
        Assert.Contains("name required", body, StringComparison.Ordinal);

        await app.StopAsync();
    }

    private sealed class TeapotMapping : IExceptionMapping
    {
        public bool TryMap(Exception exception, out int statusCode, out string title, out string? detail)
        {
            if (exception is InvalidOperationException)
            {
                statusCode = 418;
                title = "teapot";
                detail = "short and stout";
                return true;
            }

            statusCode = 0;
            title = string.Empty;
            detail = null;
            return false;
        }
    }

    private sealed class ValidationFailure : Exception
    {
        public ValidationFailure() : base("validation failed")
        {
        }

        public IReadOnlyList<string> Errors { get; } = ["name required", "age invalid"];
    }
}
