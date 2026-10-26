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
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _versions = new();
        private long _versionSequence;

        public FakeApolloHandler(
            Dictionary<string, (string, Dictionary<string, string>)> namespaces) => _namespaces = namespaces;

        public int NotificationRequests { get; private set; }

        public bool NotificationsUnsupported { get; init; }

        public string? LastNotificationsQuery { get; private set; }

        public void Update(string @namespace, string releaseKey, Dictionary<string, string> configurations)
        {
            _namespaces[@namespace] = (releaseKey, configurations);
            _versions[@namespace] = Interlocked.Increment(ref _versionSequence);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/notifications/v2")
            {
                NotificationRequests++;

                if (NotificationsUnsupported)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                var raw = GetQueryValue(request.RequestUri, "notifications");
                LastNotificationsQuery = Uri.UnescapeDataString(raw);
                var requested = System.Text.Json.JsonSerializer.Deserialize<NotificationRequest[]>(
                    LastNotificationsQuery,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

                var changed = requested
                    .Select(item => new
                    {
                        NamespaceName = item.NamespaceName,
                        Requested = item.NotificationId,
                        Current = _versions.GetValueOrDefault(item.NamespaceName ?? string.Empty, 0),
                    })
                    .Where(item => item.NamespaceName is not null && item.Requested != item.Current)
                    .Select(item => new { namespaceName = item.NamespaceName, notificationId = item.Current })
                    .ToArray();

                if (changed.Length == 0)
                {
                    await Task.Delay(50, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(changed)),
                };
            }

            // /configs/{appId}/{cluster}/{namespace}
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var @namespace = segments.Length >= 4 ? segments[3] : string.Empty;

            if (!_namespaces.TryGetValue(@namespace, out var config))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var payload = $$"""
                {
                    "appId": "myapp",
                    "cluster": "default",
                    "namespaceName": "{{@namespace}}",
                    "releaseKey": "{{config.Item1}}",
                    "configurations": {{System.Text.Json.JsonSerializer.Serialize(config.Item2)}}
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload),
            };
        }

        private static string GetQueryValue(Uri uri, string name) =>
            uri.Query.TrimStart('?')
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .FirstOrDefault(parts => parts.Length == 2 && parts[0] == name) is { } parts
                    ? parts[1]
                    : string.Empty;

        private sealed class NotificationRequest
        {
            public string? NamespaceName { get; set; }

            public long NotificationId { get; set; }
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
                options.UseLongPolling = false;   // 该组用例验证定时回退路径
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

    [Fact]
    public void Polling_interval_must_be_positive()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder()
                .AddLedonApollo(options =>
                {
                    options.ConfigServer = "http://apollo.test";
                    options.AppId = "myapp";
                    options.PollingInterval = TimeSpan.Zero;
                })
                .Build());
    }

    [Fact]
    public void Blank_namespace_fails_fast()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder()
                .AddLedonApollo(options =>
                {
                    options.ConfigServer = "http://apollo.test";
                    options.AppId = "myapp";
                    options.Namespaces = ["application", " "];
                })
                .Build());
    }

    [Fact]
    public async Task Removed_keys_disappear_after_reload()
    {
        var handler = new FakeApolloHandler(new()
        {
            ["application"] = ("r1", new Dictionary<string, string>
            {
                ["A"] = "1",
                ["B"] = "2",
            }),
        });

        var configuration = Build(handler, ["application"]);
        Assert.Equal("2", configuration["B"]);

        handler.Update("application", "r2", new Dictionary<string, string> { ["A"] = "1" });

        string? value = "2";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            value = configuration["B"];
            if (value is null)
                break;

            await Task.Delay(50);
        }

        // 快照整体重建：旧键必须消失，不能残留上一轮的内容
        Assert.Null(value);
        Assert.Equal("1", configuration["A"]);
    }

    [Fact]
    public async Task Long_polling_detects_config_change()
    {
        var handler = new FakeApolloHandler(new()
        {
            ["application"] = ("r1", new Dictionary<string, string> { ["Value"] = "first" }),
        });

        var configuration = new ConfigurationBuilder()
            .AddLedonApollo(options =>
            {
                options.ConfigServer = "http://apollo.test";
                options.AppId = "myapp";
                options.Namespaces = ["application"];
                options.UseLongPolling = true;
                options.LongPollingTimeout = TimeSpan.FromSeconds(2);
                options.PollingInterval = TimeSpan.FromMilliseconds(100);
                options.HttpMessageHandler = handler;
            })
            .Build();

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
        Assert.True(handler.NotificationRequests > 0, "未走 Apollo notifications/v2 长轮询");
        Assert.Contains("namespaceName", handler.LastNotificationsQuery);
        Assert.Contains("notificationId", handler.LastNotificationsQuery);
    }

    [Fact]
    public async Task Long_polling_falls_back_when_notifications_are_unsupported()
    {
        var handler = new FakeApolloHandler(new()
        {
            ["application"] = ("r1", new Dictionary<string, string> { ["Value"] = "first" }),
        })
        {
            NotificationsUnsupported = true,
        };

        var configuration = new ConfigurationBuilder()
            .AddLedonApollo(options =>
            {
                options.ConfigServer = "http://apollo.test";
                options.AppId = "myapp";
                options.Namespaces = ["application"];
                options.UseLongPolling = true;
                options.LongPollingTimeout = TimeSpan.FromMilliseconds(100);
                options.PollingInterval = TimeSpan.FromMilliseconds(100);
                options.HttpMessageHandler = handler;
            })
            .Build();

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
        Assert.True(handler.NotificationRequests > 0, "未尝试通知长轮询后回退");
    }
}
