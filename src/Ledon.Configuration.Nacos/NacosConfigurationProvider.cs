using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    private bool _longPollingEnabled;

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
        _longPollingEnabled = options.UseLongPolling;
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
        if (_longPollingEnabled)
            await LongPollLoopAsync().ConfigureAwait(false);
        else
            await IntervalLoopAsync().ConfigureAwait(false);
    }

    private async Task IntervalLoopAsync()
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

    private async Task LongPollLoopAsync()
    {
        while (!_stopping.IsCancellationRequested && _longPollingEnabled)
        {
            try
            {
                var changed = await ListenAsync(_stopping.Token).ConfigureAwait(false);

                if (changed is null)
                {
                    // 服务端不支持 listener（旧版/代理）：永久回退到定时轮询
                    _longPollingEnabled = false;
                    break;
                }

                if (changed.Count == 0)
                {
                    // 服务端立即返回空响应时避免热循环
                    await Task.Delay(TimeSpan.FromMilliseconds(200), _stopping.Token).ConfigureAwait(false);
                    continue;
                }

                await ApplyChangesAsync(changed, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // 长轮询失败：短暂退避后重试，旧配置继续生效
                try
                {
                    await Task.Delay(
                        _options.PollingInterval < TimeSpan.FromSeconds(5)
                            ? _options.PollingInterval
                            : TimeSpan.FromSeconds(5),
                        _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        // 回退路径
        await IntervalLoopAsync().ConfigureAwait(false);
    }

    /// <summary>向 Nacos listener 发起一次长轮询；返回变更的 dataId 集合，服务端不支持时返回 null。</summary>
    private async Task<IReadOnlyCollection<string>?> ListenAsync(CancellationToken cancellationToken)
    {
        var listening = BuildListeningConfigs();
        var serverErrors = new List<Exception>();

        foreach (var server in _options.ServerAddresses)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{server.TrimEnd('/')}/nacos/v1/cs/configs/listener" +
                (string.IsNullOrEmpty(_accessToken)
                    ? string.Empty
                    : $"?accessToken={Uri.EscapeDataString(_accessToken)}"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Listening-Configs"] = listening,
                }),
            };

            request.Headers.TryAddWithoutValidation(
                "Long-Pulling-Timeout",
                ((long)_options.LongPollingTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(_accessToken))
                request.Headers.TryAddWithoutValidation("accessToken", _accessToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.LongPollingTimeout + TimeSpan.FromSeconds(5));

            try
            {
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.NotFound
                    or HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.NotImplemented)
                {
                    return null;   // listener 端点不存在 → 回退定时轮询
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && !string.IsNullOrEmpty(_options.UserName))
                {
                    await LoginAsync(server, cancellationToken).ConfigureAwait(false);
                    continue;   // 换 token 后重试
                }

                if (!response.IsSuccessStatusCode)
                {
                    serverErrors.Add(new HttpRequestException($"Nacos listener 返回 {(int)response.StatusCode}"));
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(body))
                    return [];

                var known = _options.DataIds.ToHashSet(StringComparer.Ordinal);
                var normalized = body
                    .Replace("%01", "\u0001")
                    .Replace("%02", "\u0002");

                var changed = normalized
                    .Split('\u0001', StringSplitOptions.RemoveEmptyEntries)
                    .Select(entry => entry.Split('\u0002', 2)[0])
                    .Where(known.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                return changed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 长轮询超时属于正常语义：没有变更
                return [];
            }
            catch (HttpRequestException ex)
            {
                serverErrors.Add(ex);
            }
        }

        throw new InvalidOperationException("无法连接 Nacos listener", serverErrors.FirstOrDefault());
    }

    private async Task ApplyChangesAsync(IReadOnlyCollection<string> changed, CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var dataId in changed)
            {
                var content = await GetConfigAsync(dataId, cancellationToken).ConfigureAwait(false);

                if (_contents.TryGetValue(dataId, out var current)
                    && string.Equals(current, content, StringComparison.Ordinal))
                {
                    continue;
                }

                _contents[dataId] = content;
            }

            ApplySnapshot();
            OnReload();
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private string BuildListeningConfigs()
    {
        var entries = _options.DataIds.Select(dataId =>
        {
            var content = _contents.TryGetValue(dataId, out var value) ? value : string.Empty;
            var entry = $"{dataId}\u0002{_options.Group}\u0002{ComputeMd5(content)}";

            // Nacos 协议：tenant 非空时追加，最后用 LINE_SEPARATOR（\u0001）分隔
            if (!string.IsNullOrEmpty(_options.Namespace))
                entry += $"\u0002{_options.Namespace}";

            return entry;
        });

        return string.Join('\u0001', entries) + '\u0001';
    }

    private static string ComputeMd5(string content) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

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

                if (finalResponse.StatusCode == HttpStatusCode.NotFound)
                    return string.Empty;   // dataId 尚不存在：按空内容参与 MD5/变更检测

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
