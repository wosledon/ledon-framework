using System.Net;
using System.Net.Http.Json;
using Ledon.Data;
using Ledon.Module;
using Ledon.Module.Hosting;
using Ledon.Web;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>
/// Web 框架全链路测试。每个用例自建 TestServer 宿主，真实走一遍
/// AddLedonWeb → 自动发现 → UseLedonWeb → StartAsync → 前置任务 → 模块初始化。
/// </summary>
public class WebFrameworkTests : IDisposable
{
    private IHost? _host;
    private HttpClient? _client;

    /// <summary>按框架标准两行用法搭建宿主并启动，返回测试客户端。</summary>
    private async Task<HttpClient> StartServerAsync(
        Action<Ledon.Web.LedonWebOptions>? configure = null,
        bool withData = true,
        Action<Ledon.Module.Hosting.LedonOptions>? configureModules = null)
    {
        // 每个用例独立数据库文件，避免 SQLite 文件锁与状态串扰
        TodoDbPath.Value = Path.Combine(AppContext.BaseDirectory, $"ledon-webtest-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb(configure, configureModules);
        if (withData)
            builder.Services.AddLedonData();   // 按需引入数据访问

        var app = builder.Build();
        app.UseLedonWeb();

        await app.StartAsync();
        _host = app;
        _client = app.GetTestClient();
        return _client;
    }

    [Fact]
    public async Task Minimal_api_endpoint_is_auto_discovered_and_mapped()
    {
        var client = await StartServerAsync();

        Assert.Equal("world", await client.GetStringAsync("/hello"));
    }

    [Fact]
    public async Task Controller_is_auto_discovered()
    {
        var client = await StartServerAsync();

        Assert.Equal("pong", await client.GetStringAsync("/api/ping"));
    }

    [Fact]
    public async Task OpenApi_is_served_when_opt_in()
    {
        var client = await StartServerAsync(o => o.UseOpenApi = true);

        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"/hello\"", document);
    }

    [Fact]
    public async Task OpenApi_is_not_served_by_default()
    {
        var client = await StartServerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Data_flows_end_to_end_with_auto_migration_and_pre_initialization()
    {
        // 每个用例独立数据库文件（StartServerAsync 内生成），天然是干净起点
        DbReady.SchemaReady = false;

        // DbConsumerModule 是显式注册的探针：初始化时直接查库
        var client = await StartServerAsync(configureModules: m => m.AddModule<Fixtures.DbConsumerModule>());

        // 查库成功 → 证明 schema 先于模块就绪（ILedonPreInitialization 前置任务生效）
        Assert.True(DbReady.SchemaReady);

        var todos = await client.GetFromJsonAsync<List<TodoItem>>("/api/todos");
        Assert.NotNull(todos);
        Assert.Empty(todos);

        var created = await client.PostAsJsonAsync("/api/todos", new TodoItem { Title = "体验 Ledon" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        todos = await client.GetFromJsonAsync<List<TodoItem>>("/api/todos");
        Assert.NotNull(todos);
        Assert.Single(todos);
        Assert.Equal("体验 Ledon", todos[0].Title);
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_absent_and_echoed_when_present()
    {
        var client = await StartServerAsync();

        // 未带关联 ID：响应头里生成一个（非空）
        var generated = await client.GetAsync("/hello");
        Assert.True(generated.Headers.TryGetValues("X-Correlation-Id", out var generatedValues));
        Assert.False(string.IsNullOrWhiteSpace(generatedValues.First()));

        // 带上游关联 ID：原样回显（跨服务串链）
        var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
        request.Headers.Add("X-Correlation-Id", "upstream-trace-123");
        var echoed = await client.SendAsync(request);

        Assert.Equal("upstream-trace-123", echoed.Headers.GetValues("X-Correlation-Id").First());
    }

    // ---------- API 规范化 ----------

    [Fact]
    public async Task Unhandled_exception_returns_problem_details_with_correlation_id()
    {
        var client = await StartServerAsync();

        var response = await client.GetAsync("/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("correlationId", body);
        Assert.Contains("traceId", body);

        // ProblemDetails 里的关联 ID 与响应头一致
        var correlationId = response.Headers.GetValues("X-Correlation-Id").First();
        Assert.Contains(correlationId, body);
    }

    [Fact]
    public async Task Invalid_model_returns_validation_problem_details()
    {
        var client = await StartServerAsync();

        var response = await client.PostAsJsonAsync("/api/echo", new EchoRequest { Name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("errors", body);
        Assert.Contains("Name", body);
    }

    [Fact]
    public async Task Unified_response_wraps_payload_when_enabled()
    {
        var client = await StartServerAsync(o => o.UseUnifiedResponse = true);

        var response = await client.GetStringAsync("/api/values");

        // 信封结构：{"code":0,"data":{"value":42}}
        Assert.Contains("\"code\":0", response);
        Assert.Contains("\"value\":42", response);
    }

    [Fact]
    public async Task Api_versioning_routes_by_url_segment()
    {
        var client = await StartServerAsync(o => o.UseApiVersioning = true);

        Assert.Equal("v1", await client.GetStringAsync("/api/v1/versioned"));

        // 未定义的版本 → 404（无端点匹配）
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/versioned")).StatusCode);
    }

    public void Dispose()
    {
        _client?.Dispose();

        if (_host is not null)
        {
            // 停止宿主：模块逆序关闭路径也一并覆盖
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
