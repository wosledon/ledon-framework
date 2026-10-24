using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Ledon.Configuration.Nacos;

/// <summary>Nacos 配置源。</summary>
internal sealed class NacosConfigurationSource(NacosOptions options) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new NacosConfigurationProvider(options);
}

/// <summary>
/// Nacos 配置提供器：加载期拉取全部 DataId，之后按轮询间隔检测变更并热更新（触发重载令牌）。
/// </summary>
internal sealed class NacosConfigurationProvider : ConfigurationProvider
{
    private readonly NacosOptions _options;
    private readonly HttpClient _http;
    private readonly Timer _timer;

    // dataId → 最近一次内容（变更检测）；dataId → 该配置贡献的键（变更时先移除旧键）
    private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _keysByDataId = new(StringComparer.Ordinal);
    private string? _accessToken;

    public NacosConfigurationProvider(NacosOptions options)
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
        foreach (var dataId in _options.DataIds)
        {
            var content = GetConfigAsync(dataId).GetAwaiter().GetResult();
            Apply(dataId, content);
        }

        OnReload();

        // 启动变更轮询
        _timer.Change(_options.PollingInterval, _options.PollingInterval);
    }

    private void Apply(string dataId, string content)
    {
        _contents[dataId] = content;

        if (_keysByDataId.Remove(dataId, out var oldKeys))
            foreach (var key in oldKeys)
                Data.Remove(key);

        var keys = new List<string>();
        foreach (var (key, value) in NacosConfigurationParser.Parse(content))
        {
            Data[key] = value;
            keys.Add(key);
        }

        _keysByDataId[dataId] = keys;
    }

    private async Task ReloadSafelyAsync()
    {
        try
        {
            foreach (var (dataId, content) in _contents)
            {
                var latest = await GetConfigAsync(dataId).ConfigureAwait(false);
                if (latest == content)
                    continue;

                Apply(dataId, latest);
            }

            OnReload();
        }
        catch (Exception)
        {
            // 轮询失败不抛（Nacos 短暂不可达）：下个周期重试，旧配置继续生效
        }
    }

    private async Task<string> GetConfigAsync(string dataId)
    {
        Exception? lastError = null;

        foreach (var server in _options.ServerAddresses)
        {
            try
            {
                var response = await GetWithAuthAsync(BuildConfigUrl(server, dataId, _accessToken)).ConfigureAwait(false);

                // 401：登录换 token 后重试一次
                if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(_options.UserName))
                {
                    await LoginAsync(server).ConfigureAwait(false);
                    response = await GetWithAuthAsync(BuildConfigUrl(server, dataId, _accessToken)).ConfigureAwait(false);
                }

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                lastError = new HttpRequestException($"Nacos 返回 {(int)response.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;   // 尝试下一个地址
            }
        }

        throw new InvalidOperationException($"无法从 Nacos 读取配置 {dataId}", lastError);
    }

    private Task<HttpResponseMessage> GetWithAuthAsync(string url) => _http.GetAsync(url);

    private string BuildConfigUrl(string server, string dataId, string? accessToken) =>
        $"{server.TrimEnd('/')}/nacos/v1/cs/configs" +
        $"?dataId={Uri.EscapeDataString(dataId)}" +
        $"&group={Uri.EscapeDataString(_options.Group)}" +
        $"&tenant={Uri.EscapeDataString(_options.Namespace)}" +
        $"&accessToken={accessToken}";

    private async Task LoginAsync(string server)
    {
        var response = await _http.PostAsync(
            $"{server.TrimEnd('/')}/nacos/v1/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["userName"] = _options.UserName!,
                ["password"] = _options.Password!,
            }));

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        _accessToken = payload.TryGetProperty("accessToken", out var token) ? token.GetString() : null;
    }
}
