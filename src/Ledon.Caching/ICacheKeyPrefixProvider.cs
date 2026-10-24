namespace Ledon.Caching;

/// <summary>
/// 缓存键前缀提供者（可选）。多租户场景下让缓存键按租户隔离——
/// 不实现时用 <see cref="LedonCachingOptions.KeyPrefix"/> 静态前缀。
/// 每次缓存操作都会调用（因此支持按请求/按作用域变化的值）。
/// </summary>
public interface ICacheKeyPrefixProvider
{
    /// <summary>当前 key 前缀；返回 null/空表示用静态配置的前缀。</summary>
    string? GetKeyPrefix();
}
