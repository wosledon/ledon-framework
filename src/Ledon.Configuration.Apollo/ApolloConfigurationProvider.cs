using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Ledon.Configuration.Apollo;

/// <summary>Apollo 配置源。</summary>
internal sealed class ApolloConfigurationSource(ApolloOptions options) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new ApolloConfigurationProvider(options);
}

/// <summary>
/// Apollo 配置提供器：加载期拉取全部命名空间，之后按轮询间隔检测变更并热更新（触发重载令牌）。
/// 非 application 命名空间的原始内容以 <c>{命名空间}:content</c> 键暴露（properties 之外的格式自行解析）。
/// </summary>
internal sealed class ApolloConfigurationProvider : ConfigurationProvider
{
    private readonly ApolloOptions _options;
    private readonly HttpClient _http;
    private readonly Timer _timer;

    // 命名空间 → releaseKey（变更检测）；命名空间 → 该命名空间贡献的键
    private readonly Dictionary<string, string> _releaseKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _keysByNamespace = new(StringComparer.Ordinal);

    public ApolloConfigurationProvider(ApolloOptions options)
    {
        _options = options;
        _http = new HttpClient(options.HttpMessageHandler ?? new SocketsHttpHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _timer = new Timer(_ => _ = ReloadSafelyAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public override void Load()
    {
        foreach (var @namespace in _options.Namespaces)
            Apply(@namespace, FetchAsync(@namespace).GetAwaiter().GetResult());

        OnReload();
        _timer.Change(_options.PollingInterval, _options.PollingInterval);
    }

    private void Apply(string @namespace, ApolloConfigResponse config)
    {
        _releaseKeys[@namespace] = config.ReleaseKey ?? string.Empty;

        if (_keysByNamespace.Remove(@namespace, out var oldKeys))
            foreach (var key in oldKeys)
                Data.Remove(key);

        var keys = new List<string>();

        foreach (var (key, value) in config.Configurations)
        {
            var configKey = @namespace == "application" ? key : $"{@namespace}:{key}";
            Data[configKey] = value;
            keys.Add(configKey);
        }

        _keysByNamespace[@namespace] = keys;
    }

    private async Task ReloadSafelyAsync()
    {
        try
        {
            foreach (var @namespace in _options.Namespaces)
            {
                var config = await FetchAsync(@namespace);

                if (config.ReleaseKey == _releaseKeys[@namespace])
                    continue;

                Apply(@namespace, config);
            }

            OnReload();
        }
        catch (Exception)
        {
            // 轮询失败不抛（Apollo 短暂不可达）：下个周期重试，旧配置继续生效
        }
    }

    private async Task<ApolloConfigResponse> FetchAsync(string @namespace)
    {
        var url = $"{_options.ConfigServer.TrimEnd('/')}/configs/{Uri.EscapeDataString(_options.AppId)}" +
                  $"/{Uri.EscapeDataString(_options.Cluster)}/{Uri.EscapeDataString(@namespace)}";

        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(payload))
            return new ApolloConfigResponse(string.Empty, []);

        var config = JsonSerializer.Deserialize<ApolloConfigResponse>(
            payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return config ?? new ApolloConfigResponse(string.Empty, []);
    }

    private sealed record ApolloConfigResponse(string ReleaseKey, Dictionary<string, string> Configurations);
}
