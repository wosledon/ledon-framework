using System.Net;
using Ledon.ApiVersioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>API 版本化边界：URL 段、非法默认版本、无 Sunset 的弃用头。</summary>
public class ApiVersioningEdgeTests
{
    [Fact]
    public void Url_segment_reader_is_optional()
    {
        var reader = ApiVersionReaders.Create(new LedonApiVersioningOptions { UseUrlSegment = false });
        var context = new DefaultHttpContext();
        context.Request.Path = "/v2/orders";

        Assert.Empty(reader.Read(context.Request));

        context.Request.Headers["X-Api-Version"] = "2.0";
        Assert.Contains("2.0", reader.Read(context.Request));
    }

    [Fact]
    public void Invalid_default_version_fails_at_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddLedonApiVersioning(options => options.DefaultVersion = "not-a-version"));
    }

    [Fact]
    public async Task Deprecation_without_sunset_omits_sunset_header()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonApiVersioning();

        await using var app = builder.Build();
        app.UseRouting();
        app.UseLedonApiVersioning();
        app.MapGet("/legacy", () => "ok")
            .WithMetadata(new ApiDeprecatedAttribute(link: "https://docs.example.com/migrate"));

        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/legacy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Deprecation").Single());
        Assert.False(response.Headers.Contains("Sunset"));
        Assert.Contains("rel=\"deprecation\"", response.Headers.GetValues("Link").Single());
    }
}
