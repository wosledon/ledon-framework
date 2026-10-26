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
/// 每次变更都构建全新快照再原子替换，避免读者在枚举时看到半更新的字典。
/// </summary>
internal sealed class NacosConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly NacosOptions _options;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    // dataId → 最近一次内容（变更检测）
    private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);

    private string? _accessToken;
    private Task? _polling;

    public NacosConfigurationProvider(NacosOptions options)
    {
        _options = options;
        _http = new HttpClient(
            options.HttpMessageHandler ?? new SocketsHttpHandler(),
            disposeHandler: options.HttpMessageHandler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // 直接指定 token 时跳过登录（NacosOptions.AccessToken 文档承诺的行为）
        _accessToken = options.AccessToken;
    }

    public override void Load()
    {
        _reloadGate.Wait();

        try
        {
            _contents.Clear();

            // 同步加载首个快照；失败让宿主启动失败（配置中心不可达不应该带病启动）
            foreach (var dataId in _options.DataIds)
                _contents[dataId] = GetConfigAsync(dataId, CancellationToken.None).GetAwaiter().GetResult();

            ApplySnapshot();
        }
        finally
        {
            _reloadGate.Release();
        }

        OnReload();
        _polling ??= PollAsync();
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(_options.PollingInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                await ReloadSafelyAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private async Task ReloadSafelyAsync()
    {
        await _reloadGate.WaitAsync(_stopping.Token).ConfigureAwait(false);

        try
        {
            var changed = false;

            foreach (var dataId in _options.DataIds)
            {
                var latest = await GetConfigAsync(dataId, _stopping.Token).ConfigureAwait(false);

                if (_contents.TryGetValue(dataId, out var current)
                    && string.Equals(current, latest, StringComparison.Ordinal))
                {
                    continue;
                }

                _contents[dataId] = latest;
                changed = true;
            }

            if (!changed)
                return;

            ApplySnapshot();
            OnReload();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // 停机中取消
        }
        catch (Exception)
        {
            // 轮询失败不抛（Nacos 短暂不可达）：下个周期重试，旧配置继续生效
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>用当前内容构建全新快照并原子替换（SetData 替换字典引用，读者不会看到半更新状态）。</summary>
    private void ApplySnapshot()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var dataId in _options.DataIds)
        {
            if (!_contents.TryGetValue(dataId, out var content))
                continue;

            foreach (var (key, value) in NacosConfigurationParser.Parse(content))
                data[key] = value;
        }

        // 整体替换字典引用（读者拿到的要么是旧快照，要么是新快照）
        Data = data;
    }

    private async Task<string> GetConfigAsync(string dataId, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var server in _options.ServerAddresses)
        {
            try
            {
                var response = await GetWithAuthAsync(
                    BuildConfigUrl(server, dataId, _accessToken), cancellationToken).ConfigureAwait(false);
                using var responseLease = response;

                var finalResponse = response;

                // 401：登录换 token 后重试一次
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && !string.IsNullOrEmpty(_options.UserName))
                {
                    await LoginAsync(server, cancellationToken).ConfigureAwait(false);
                    finalResponse = await GetWithAuthAsync(
                        BuildConfigUrl(server, dataId, _accessToken), cancellationToken).ConfigureAwait(false);
                }

                using var retry = ReferenceEquals(finalResponse, response) ? null : finalResponse;

                if (finalResponse.IsSuccessStatusCode)
                    return await finalResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                lastError = new HttpRequestException($"Nacos 返回 {(int)finalResponse.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;   // 尝试下一个地址
            }
        }

        throw new InvalidOperationException($"无法从 Nacos 读取配置 {dataId}", lastError);
    }

    private Task<HttpResponseMessage> GetWithAuthAsync(string url, CancellationToken cancellationToken) =>
        _http.GetAsync(url, cancellationToken);

    private string BuildConfigUrl(string server, string dataId, string? accessToken)
    {
        var url = $"{server.TrimEnd('/')}/nacos/v1/cs/configs" +
                  $"?dataId={Uri.EscapeDataString(dataId)}" +
                  $"&group={Uri.EscapeDataString(_options.Group)}" +
                  $"&tenant={Uri.EscapeDataString(_options.Namespace)}";

        return string.IsNullOrEmpty(accessToken)
            ? url
            : $"{url}&accessToken={Uri.EscapeDataString(accessToken)}";
    }

    private async Task LoginAsync(string server, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsync(
            $"{server.TrimEnd('/')}/nacos/v1/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["userName"] = _options.UserName!,
                ["password"] = _options.Password!,
            }),
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        _accessToken = payload.TryGetProperty("accessToken", out var token) ? token.GetString() : null;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _http.Dispose();
        // CTS/信号量不 Dispose：轮询任务可能仍在观察 Token / finally 中 Release，
        // 二者都不持有未托管资源，交给 GC 比冒 ObjectDisposedException 更安全。
    }
}
