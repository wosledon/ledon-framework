using System.Net;
using System.Net.Http.Json;
using FluentValidation;
using Ledon.Data;
using Ledon.Web;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>FluentValidation 集成测试：验证器自动发现、统一 400 ValidationProblemDetails。</summary>
public class FluentValidationTests : IDisposable
{
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync()
    {
        TodoDbPath.Value = Path.Combine(AppContext.BaseDirectory, $"ledon-fv-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonData();
        builder.Services.AddLedonFluentValidation();   // EchoRequestValidator 自动发现

        var app = builder.Build();
        app.UseLedonWeb();

        await app.StartAsync();
        _app = app;
        return app.GetTestClient();
    }

    [Fact]
    public async Task FluentValidation_rules_produce_validation_problem_details()
    {
        var client = await StartAsync();

        // FV 规则触发（DataAnnotations 没有的规则）：forbidden → 400 ValidationProblemDetails
        var rejected = await client.PostAsJsonAsync("/api/echo", new EchoRequest { Name = "forbidden" });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Contains("errors", await rejected.Content.ReadAsStringAsync());

        // 合法值 → 200（回显）
        var accepted = await client.PostAsJsonAsync("/api/echo", new EchoRequest { Name = "ok" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task FluentValidation_validators_are_auto_discovered()
    {
        await using var provider = new ServiceCollection()
            .AddLedonFluentValidation()
            .BuildServiceProvider();

        // 验证器经允许列表扫描自动注册（IValidator<EchoRequest> 可解析）
        Assert.NotNull(provider.GetRequiredService<IValidator<EchoRequest>>());
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}
