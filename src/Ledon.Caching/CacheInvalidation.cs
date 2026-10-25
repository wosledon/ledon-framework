namespace Ledon.Caching;

/// <summary>缓存失效广播消息（跨实例一致性用）。</summary>
/// <param name="Kind"><c>key</c> 单键失效；<c>prefix</c> 前缀失效。</param>
/// <param name="Value">键或前缀（不含进程内完整键的静态前缀部分）。</param>
public sealed record CacheInvalidationMessage(string Kind, string Value)
{
    /// <summary>单键失效消息。</summary>
    public static CacheInvalidationMessage ForKey(string key) => new("key", key);

    /// <summary>前缀失效消息。</summary>
    public static CacheInvalidationMessage ForPrefix(string prefix) => new("prefix", prefix);
}

/// <summary>
/// 缓存失效总线：本实例删了某个键/前缀时，把失效广播给其它实例（多实例部署下
/// 各实例的 L1 内存缓存保持一致）。默认进程内实现（不跨实例）；
/// 配了 Redis 后走发布订阅。
/// </summary>
public interface ICacheInvalidationBus
{
    /// <summary>广播"某键已失效"。</summary>
    Task PublishKeyRemovedAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>广播"某前缀下全部失效"。</summary>
    Task PublishPrefixRemovedAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>登记本地应用器（缓存实现构造时调用一次：收到广播后应用到本进程缓存）。</summary>
    void SetLocalApplier(Action<string> removeKey, Action<string> removePrefix);
}

/// <summary>进程内总线：不跨实例（单实例部署的默认行为，零依赖）。</summary>
public sealed class InProcessCacheInvalidationBus : ICacheInvalidationBus
{
    private Action<string>? _removeKey;
    private Action<string>? _removePrefix;

    public Task PublishKeyRemovedAsync(string key, CancellationToken cancellationToken = default)
    {
        _removeKey?.Invoke(key);
        return Task.CompletedTask;
    }

    public Task PublishPrefixRemovedAsync(string prefix, CancellationToken cancellationToken = default)
    {
        _removePrefix?.Invoke(prefix);
        return Task.CompletedTask;
    }

    public void SetLocalApplier(Action<string> removeKey, Action<string> removePrefix)
    {
        _removeKey = removeKey;
        _removePrefix = removePrefix;
    }
}
