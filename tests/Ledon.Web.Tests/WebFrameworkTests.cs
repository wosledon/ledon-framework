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
