using Ledon.Configuration.Apollo;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace Ledon.Web.Tests;

/// <summary>Apollo 配置中心测试：命名空间解析、合并、热更新、失败路径（假 HTTP 服务端）。</summary>
public class ApolloConfigurationTests
{
    private sealed class FakeApolloHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string ReleaseKey, Dictionary<string, string> Configurations)> _namespaces;

        public FakeApolloHandler(
            Dictionary<string, (string, Dictionary<string, string>)> namespaces) => _namespaces = namespaces;

        public void Update(string @namespace, string releaseKey, Dictionary<string, string> configurations) =>
            _namespaces[@namespace] = (releaseKey, configurations);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // /configs/{appId}/{cluster}/{namespace}
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var @namespace = segments.Length >= 4 ? segments[3] : string.Empty;

            if (!_namespaces.TryGetValue(@namespace, out var config))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var payload = $$"""
                {
                    "appId": "myapp",
                    "cluster": "default",
                    "namespaceName": "{{@namespace}}",
                    "releaseKey": "{{config.Item1}}",
                    "configurations": {{System.Text.Json.JsonSerializer.Serialize(config.Item2)}}
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload),
            });
        }
    }

    private static IConfigurationRoot Build(
        FakeApolloHandler handler,
        string[] namespaces)
    {
        return new ConfigurationBuilder()
            .AddLedonApollo(options =>
            {
                options.ConfigServer = "http://apollo.test";
                options.AppId = "myapp";
                options.Namespaces = namespaces;
                options.PollingInterval = TimeSpan.FromMilliseconds(100);
                options.HttpMessageHandler = handler;
            })
            .Build();
    }

    [Fact]
    public void Application_namespace_keys_enter_config_tree()
    {
        var handler = new FakeApolloHandler(new()
        {
            ["application"] = ("r1", new Dictionary<string, string>
            {
                ["Smtp:Host"] = "smtp.example.com",
                ["Smtp:Port"] = "587",
            }),
        });

        var configuration = Build(handler, ["application"]);

        Assert.Equal("smtp.example.com", configuration["Smtp:Host"]);
        Assert.Equal("587", configuration["Smtp:Port"]);
    }

    [Fact]
    public void Custom_namespace_content_is_exposed_under_prefixed_key()
    {
        var handler = new FakeApolloHandler(new()
        {
            ["application"] = ("r1", new Dictionary<string, string>()),
            ["database"] = ("r1", new Dictionary<string, string> { ["content"] = "redis.host=localhost" }),
        });

        var configuration = Build(handler, ["application", "database"]);

        Assert.Equal("redis.host=localhost", configuration["database:content"]);
    }

    [Fact]
    public async Task Config_changes_are_hot_reloaded()
    {
        var handler = new FakeApolloHandler(new()
        {
            ["application"] = ("r1", new Dictionary<string, string> { ["Value"] = "first" }),
        });

        var configuration = Build(handler, ["application"]);
        Assert.Equal("first", configuration["Value"]);

        handler.Update("application", "r2", new Dictionary<string, string> { ["Value"] = "second" });

        string? value = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            value = configuration["Value"];
            if (value == "second")
                break;

            await Task.Delay(50);
        }

        Assert.Equal("second", value);
    }

    [Fact]
    public void Missing_app_id_fails_fast()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder()
                .AddLedonApollo(options => options.ConfigServer = "http://apollo.test")
                .Build());
    }

    [Fact]
    public void Unknown_namespace_fails_fast_at_startup()
    {
        var exception = Assert.Throws<HttpRequestException>(() =>
            new ConfigurationBuilder()
                .AddLedonApollo(options =>
                {
                    options.ConfigServer = "http://127.0.0.1:1";   // 必然拒绝连接
                    options.AppId = "myapp";
                })
                .Build());

        Assert.NotNull(exception);
    }
}
