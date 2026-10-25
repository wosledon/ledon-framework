using System.Text.Json;
using StackExchange.Redis;

namespace Ledon.Caching;

/// <summary>
/// 基于 Redis 发布订阅的失效总线：多实例部署下，任一本实例删键/删前缀，
/// 其它实例的 L1 内存缓存同步失效（避免"实例 A 已更新、实例 B 还返回旧值"）。
/// </summary>
public sealed class RedisCacheInvalidationBus : ICacheInvalidationBus, IAsyncDisposable
{
    private readonly LedonCachingOptions _options;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisChannel _channel;
    private Action<string>? _removeKey;
    private Action<string>? _removePrefix;

    public RedisCacheInvalidationBus(LedonCachingOptions options)
    {
        _options = options;
        _multiplexer = ConnectionMultiplexer.Connect(options.RedisConnectionString!);
        _channel = RedisChannel.Literal($"{options.KeyPrefix ?? "ledon"}:cache:invalidation");

        // 订阅：收到广播 → 应用到本进程缓存
        _multiplexer.GetSubscriber().Subscribe(_channel).OnMessage(async message =>
        {
            var payload = (string?)message.Message;
            if (string.IsNullOrEmpty(payload))
                return;

            var invalidated = JsonSerializer.Deserialize<CacheInvalidationMessage>(payload);
            if (invalidated is null)
                return;

            await Task.Yield();

            if (invalidated.Kind == "prefix")
                _removePrefix?.Invoke(invalidated.Value);
            else
                _removeKey?.Invoke(invalidated.Value);
        });
    }

    public async Task PublishKeyRemovedAsync(string key, CancellationToken cancellationToken = default) =>
        await PublishAsync(CacheInvalidationMessage.ForKey(key)).ConfigureAwait(false);

    public async Task PublishPrefixRemovedAsync(string prefix, CancellationToken cancellationToken = default) =>
        await PublishAsync(CacheInvalidationMessage.ForPrefix(prefix)).ConfigureAwait(false);

    public void SetLocalApplier(Action<string> removeKey, Action<string> removePrefix)
    {
        _removeKey = removeKey;
        _removePrefix = removePrefix;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _multiplexer.GetSubscriber().UnsubscribeAsync(_channel).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // 退订失败不影响进程退出
        }

        await _multiplexer.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PublishAsync(CacheInvalidationMessage message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(message);
            await _multiplexer.GetSubscriber().PublishAsync(_channel, payload).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // 广播失败不该影响本地删除（本地已生效，最坏是其它实例多留一会儿旧值）
        }
    }


}
