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

    // 命名空间 → 最近一次 notificationId（长轮询用）
    private readonly Dictionary<string, long> _notificationIds = new(StringComparer.Ordinal);

    private Task? _polling;
    private bool _longPollingEnabled;

    public ApolloConfigurationProvider(ApolloOptions options)
    {
        _options = options;
        _longPollingEnabled = options.UseLongPolling;
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
            _notificationIds.Clear();

            // 同步加载首个快照；失败让宿主启动失败（配置中心不可达不应该带病启动）
            foreach (var @namespace in _options.Namespaces)
            {
                _configs[@namespace] = FetchAsync(@namespace, CancellationToken.None).GetAwaiter().GetResult();
                _notificationIds[@namespace] = -1;
            }

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
                    _longPollingEnabled = false;
                    break;
                }

                if (changed.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), _stopping.Token).ConfigureAwait(false);
                    continue;
                }

                await ApplyLongPollChangesAsync(changed, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
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

        await IntervalLoopAsync().ConfigureAwait(false);
    }

    /// <summary>Apollo notifications/v2 长轮询；返回变更的命名空间，服务端不支持时返回 null。</summary>
    private async Task<IReadOnlyCollection<string>?> ListenAsync(CancellationToken cancellationToken)
    {
        var notifications = _options.Namespaces
            .Select(@namespace => new
            {
                namespaceName = @namespace,
                notificationId = _notificationIds.TryGetValue(@namespace, out var id) ? id : -1,
            })
            .ToArray();

        var payload = JsonSerializer.Serialize(notifications);
        var url = $"{_options.ConfigServer.TrimEnd('/')}/notifications/v2" +
                  $"?appId={Uri.EscapeDataString(_options.AppId)}" +
                  $"&cluster={Uri.EscapeDataString(_options.Cluster)}" +
                  $"&notifications={Uri.EscapeDataString(payload)}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.LongPollingTimeout + TimeSpan.FromSeconds(5));

        using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented)
        {
            return null;
        }

        // 304：长轮询超时且无变更
        if (response.StatusCode == HttpStatusCode.NotModified)
            return [];

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body) || body == "[]")
            return [];

        var received = JsonSerializer.Deserialize<ApolloNotification[]>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        var originals = _options.Namespaces.ToHashSet(StringComparer.Ordinal);
        var changed = new List<string>();

        foreach (var notification in received)
        {
            if (notification.NamespaceName is null || !originals.Contains(notification.NamespaceName))
                continue;

            if (_notificationIds.TryGetValue(notification.NamespaceName, out var previous)
                && previous == notification.NotificationId)
            {
                continue;
            }

            _notificationIds[notification.NamespaceName] = notification.NotificationId;
            changed.Add(notification.NamespaceName);
        }

        return changed;
    }

    private async Task ApplyLongPollChangesAsync(IReadOnlyCollection<string> changed, CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var applied = false;

            foreach (var @namespace in changed)
            {
                var config = await FetchAsync(@namespace, cancellationToken).ConfigureAwait(false);
                _configs[@namespace] = config;
                applied = true;
            }

            if (!applied)
                return;

            ApplySnapshot();
            OnReload();
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private sealed class ApolloNotification
    {
        public string? NamespaceName { get; set; }

        public long NotificationId { get; set; }
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
