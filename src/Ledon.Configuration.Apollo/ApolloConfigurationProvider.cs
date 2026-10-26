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
/// application 命名空间的键直接进配置树；其它命名空间以 <c>{命名空间}:键</c> 暴露。
/// 每次变更都构建全新快照再原子替换，避免读者在枚举时看到半更新的字典。
/// </summary>
internal sealed class ApolloConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly ApolloOptions _options;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    // 命名空间 → 最近一次响应（releaseKey 用于变更检测）
    private readonly Dictionary<string, ApolloConfigResponse> _configs = new(StringComparer.Ordinal);

    private Task? _polling;

    public ApolloConfigurationProvider(ApolloOptions options)
    {
        _options = options;
        _http = new HttpClient(
            options.HttpMessageHandler ?? new SocketsHttpHandler(),
            disposeHandler: options.HttpMessageHandler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public override void Load()
    {
        _reloadGate.Wait();

        try
        {
            _configs.Clear();

            // 同步加载首个快照；失败让宿主启动失败（配置中心不可达不应该带病启动）
            foreach (var @namespace in _options.Namespaces)
                _configs[@namespace] = FetchAsync(@namespace, CancellationToken.None).GetAwaiter().GetResult();

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

            foreach (var @namespace in _options.Namespaces)
            {
                var config = await FetchAsync(@namespace, _stopping.Token).ConfigureAwait(false);

                if (_configs.TryGetValue(@namespace, out var current)
                    && string.Equals(current.ReleaseKey, config.ReleaseKey, StringComparison.Ordinal))
                {
                    continue;
                }

                _configs[@namespace] = config;
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
            // 轮询失败不抛（Apollo 短暂不可达）：下个周期重试，旧配置继续生效
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>用当前配置构建全新快照并原子替换（SetData 替换字典引用，读者不会看到半更新状态）。</summary>
    private void ApplySnapshot()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var @namespace in _options.Namespaces)
        {
            if (!_configs.TryGetValue(@namespace, out var config))
                continue;

            foreach (var (key, value) in config.Configurations ?? [])
            {
                data[@namespace == "application" ? key : $"{@namespace}:{key}"] = value;
            }
        }

        // 整体替换字典引用（读者拿到的要么是旧快照，要么是新快照）
        Data = data;
    }

    private async Task<ApolloConfigResponse> FetchAsync(string @namespace, CancellationToken cancellationToken)
    {
        var url = $"{_options.ConfigServer.TrimEnd('/')}/configs/{Uri.EscapeDataString(_options.AppId)}" +
                  $"/{Uri.EscapeDataString(_options.Cluster)}/{Uri.EscapeDataString(@namespace)}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
            return new ApolloConfigResponse(string.Empty, []);

        var config = JsonSerializer.Deserialize<ApolloConfigResponse>(
            payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return config ?? new ApolloConfigResponse(string.Empty, []);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _http.Dispose();
        // CTS/信号量不 Dispose：轮询任务可能仍在观察 Token / finally 中 Release，
        // 二者都不持有未托管资源，交给 GC 比冒 ObjectDisposedException 更安全。
    }

    private sealed record ApolloConfigResponse(string? ReleaseKey, Dictionary<string, string>? Configurations);
}
