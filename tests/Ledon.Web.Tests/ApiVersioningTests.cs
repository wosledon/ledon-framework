using System.Net;
using Ledon.ApiVersioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>API 版本化：多来源读取、弃用响应头与 DI 配置。</summary>
public class ApiVersioningTests
{
    [Fact]
    public void Reader_supports_query_header_and_media_type()
    {
        var reader = ApiVersionReaders.Create(new LedonApiVersioningOptions { UseUrlSegment = false });

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?api-version=2.0");
        Assert.Contains("2.0", reader.Read(context.Request));

        context.Request.QueryString = QueryString.Empty;
        context.Request.Headers["X-Api-Version"] = "3.0";
        Assert.Contains("3.0", reader.Read(context.Request));

        context.Request.Headers.Remove("X-Api-Version");
        context.Request.ContentType = "application/json;v=4.0";
        Assert.Contains("4.0", reader.Read(context.Request));
    }

    [Fact]
    public async Task Deprecation_middleware_adds_sunset_and_link_headers()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonApiVersioning();

        await using var app = builder.Build();
        app.UseRouting();
        app.UseLedonApiVersioning();
        app.MapGet("/legacy", () => "ok")
            .WithMetadata(new ApiDeprecatedAttribute("2026-01-01", "https://docs.example.com/migrate"));

        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/legacy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Deprecation").Single());
        Assert.Contains("2026", response.Headers.GetValues("Sunset").Single());
        Assert.Contains("rel=\"deprecation\"", response.Headers.GetValues("Link").Single());
    }

    [Fact]
    public void Registration_configures_default_version_and_readers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonApiVersioning(o =>
        {
            o.DefaultVersion = "2.0";
            o.UseUrlSegment = false;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value;

        Assert.Equal(new Asp.Versioning.ApiVersion(2, 0), options.DefaultApiVersion);
        Assert.True(options.AssumeDefaultVersionWhenUnspecified);
    }
}
