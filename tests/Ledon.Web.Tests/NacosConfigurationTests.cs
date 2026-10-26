using Ledon.Configuration.Nacos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;

namespace Ledon.Web.Tests;

/// <summary>Nacos 配置中心测试：解析、多 DataId 合并、热更新、鉴权登录（假 HTTP 服务端）。</summary>
public class NacosConfigurationTests
{
    private sealed class FakeNacosHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _configs;

        public FakeNacosHandler(Dictionary<string, string> configs) => _configs = configs;

        public int ConfigRequests { get; private set; }

        public int LoginRequests { get; private set; }

        public void Update(string dataId, string content) => _configs[dataId] = content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/nacos/v1/cs/configs")
            {
                ConfigRequests++;

                // 模拟鉴权：没有有效 accessToken 一律 401
                var token = GetQueryValue(request.RequestUri, "accessToken");
                if (RequireToken && token != "tok")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                var dataId = Uri.UnescapeDataString(GetQueryValue(request.RequestUri, "dataId"));
                var content = _configs.TryGetValue(dataId, out var value) ? value : string.Empty;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content),
                });
            }

            if (path == "/nacos/v1/auth/login")
            {
                LoginRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"accessToken\":\"tok\"}"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        /// <summary>为 true 时配置接口要求 accessToken=tok（模拟开启鉴权的 Nacos）。</summary>
        public bool RequireToken { get; init; }

        private static string GetQueryValue(Uri uri, string name) =>
            uri.Query.TrimStart('?')
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .FirstOrDefault(parts => parts.Length == 2 && parts[0] == name) is { } parts
                    ? Uri.UnescapeDataString(parts[1])
                    : string.Empty;
    }

    private static IConfigurationRoot Build(
        Dictionary<string, string> configs,
        string[] dataIds,
        Action<NacosOptions>? configure = null)
    {
        var handler = new FakeNacosHandler(configs);

        return new ConfigurationBuilder()
            .AddLedonNacos(options =>
            {
                options.ServerAddresses = ["http://nacos.test"];
                options.DataIds = dataIds;
                options.PollingInterval = TimeSpan.FromMilliseconds(100);
                options.HttpMessageHandler = handler;
                configure?.Invoke(options);
            })
            .Build();
    }

    [Fact]
    public void Properties_and_json_content_are_parsed()
    {
        var configuration = Build(
            new Dictionary<string, string>
            {
                ["app"] = "# 注释\nSmtp:Host=smtp.example.com\nSmtp:Port=587",
                ["app-redis"] = "{ \"Redis\": { \"Host\": \"localhost\", \"Port\": 6379 } }",
            },
            ["app", "app-redis"]);

        Assert.Equal("smtp.example.com", configuration["Smtp:Host"]);
        Assert.Equal("587", configuration["Smtp:Port"]);
        Assert.Equal("localhost", configuration["Redis:Host"]);
        Assert.Equal("6379", configuration["Redis:Port"]);
    }

    [Fact]
    public async Task Config_changes_are_hot_reloaded()
    {
        var configs = new Dictionary<string, string> { ["app"] = "Value=first" };
        var configuration = Build(configs, ["app"]);

        Assert.Equal("first", configuration["Value"]);

        // 服务端内容变更 → 轮询检测到 → 热更新
        configs["app"] = "Value=second";

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

    /// <summary>强类型配置的热更新：配置中心变更 → IConfiguration 重载 → IOptionsMonitor 收到新值。</summary>
    [Fact]
    public async Task Strongly_typed_options_hot_reload_via_options_monitor()
    {
        var configs = new Dictionary<string, string> { ["app"] = "Smtp:Host=first.example.com\nSmtp:Port=25" };
        var configuration = Build(configs, ["app"]);

        var services = new ServiceCollection();
        services.AddLedonOptions<SmtpOptions>(configuration);
        await using var provider = services.BuildServiceProvider();


        var monitor = provider.GetRequiredService<IOptionsMonitor<SmtpOptions>>();
        Assert.Equal("first.example.com", monitor.CurrentValue.Host);

        // 配置中心内容变更 → 轮询热更新 → IOptionsMonitor 的 CurrentValue 跟着变
        configs["app"] = "Smtp:Host=second.example.com\nSmtp:Port=25";

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.CurrentValue.Host != "second.example.com" && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal("second.example.com", monitor.CurrentValue.Host);
    }

    /// <summary>强类型选项夹具（DataAnnotations 启动校验）。</summary>
    public sealed class SmtpOptions
    {
        [Required] public string Host { get; set; } = string.Empty;

        [Range(1, 65535)] public int Port { get; set; }
    }

    [Fact]
    public void Auth_login_happens_when_credentials_configured()    {
        var handler = new FakeNacosHandler(new Dictionary<string, string> { ["app"] = "Value=v" })
        {
            RequireToken = true,   // 未带有效 token 一律 401
        };

        var configuration = new ConfigurationBuilder()
            .AddLedonNacos(options =>
            {
                options.ServerAddresses = ["http://nacos.test"];
                options.DataIds = ["app"];
                options.UserName = "nacos";
                options.Password = "nacos";
                options.HttpMessageHandler = handler;
            })
            .Build();

        // 401 → 登录换 token → 重试成功（配置加载出来了即证明链路通）
        Assert.Equal("v", configuration["Value"]);
        Assert.True(handler.LoginRequests >= 1, "未触发登录");
    }

    [Fact]
    public void Unreachable_server_fails_fast_at_startup()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder()
                .AddLedonNacos(options =>
                {
                    options.ServerAddresses = ["http://127.0.0.1:1"];   // 必然拒绝连接
                    options.DataIds = ["app"];
                })
                .Build());

        Assert.Contains("无法从 Nacos 读取配置", exception.Message);
    }

    [Fact]
    public void Missing_data_ids_fails_fast()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder()
                .AddLedonNacos(options => options.ServerAddresses = ["http://nacos.test"])
                .Build());
    }

    [Fact]
    public void Polling_interval_must_be_positive()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder()
                .AddLedonNacos(options =>
                {
                    options.ServerAddresses = ["http://nacos.test"];
                    options.DataIds = ["app"];
                    options.PollingInterval = TimeSpan.Zero;
                })
                .Build());
    }

    [Fact]
    public void User_name_requires_password()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder()
                .AddLedonNacos(options =>
                {
                    options.ServerAddresses = ["http://nacos.test"];
                    options.DataIds = ["app"];
                    options.UserName = "nacos";
                })
                .Build());
    }

    [Fact]
    public void Access_token_option_skips_login()
    {
        var handler = new FakeNacosHandler(new Dictionary<string, string> { ["app"] = "Value=v" })
        {
            RequireToken = true,
        };

        var configuration = new ConfigurationBuilder()
            .AddLedonNacos(options =>
            {
                options.ServerAddresses = ["http://nacos.test"];
                options.DataIds = ["app"];
                options.AccessToken = "tok";   // 直接指定 token：不应再走登录
                options.HttpMessageHandler = handler;
            })
            .Build();

        Assert.Equal("v", configuration["Value"]);
        Assert.Equal(0, handler.LoginRequests);
    }

    [Fact]
    public async Task Removed_keys_disappear_after_reload()
    {
        var configs = new Dictionary<string, string> { ["app"] = "A=1\nB=2" };
        var configuration = Build(configs, ["app"]);

        Assert.Equal("2", configuration["B"]);

        configs["app"] = "A=1";

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
}
