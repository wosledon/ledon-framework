using Ledon.Caching;
using Microsoft.Extensions.Logging;

namespace Ledon.Application;

/// <summary>可缓存的查询：相同的 <see cref="CacheKey"/> 在 <see cref="CacheDuration"/> 内直接返回缓存。</summary>
public interface ICacheableQuery
{
    /// <summary>缓存键（含租户/用户维度由缓存服务自动加前缀）。</summary>
    string CacheKey { get; }

    /// <summary>缓存时长。</summary>
    TimeSpan CacheDuration { get; }
}

/// <summary>
/// 缓存行为：查询实现 <see cref="ICacheableQuery"/> 即自动走缓存（single-flight 防击穿）。
/// 写命令不进缓存（缓存只对查询生效）。
/// <para>
/// 未注册 <see cref="ICacheService"/>（没引 Ledon.Caching）时直通——与
/// <see cref="TransactionBehavior{TRequest}"/> 对缺失 IUnitOfWork 的处理一致，
/// 避免"开了 UseCaching 但没引缓存包 → 所有请求 500"。
/// </para>
/// </summary>
public sealed class CachingBehavior<TRequest>(
    ICacheService? cache,
    ILogger<CachingBehavior<TRequest>> logger) : IPipelineBehavior<TRequest>
    where TRequest : notnull
{
    public async ValueTask<object?> HandleAsync(
        TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken = default)
    {
        if (cache is null || request is not ICacheableQuery cacheable)
        {
            if (cache is null)
                logger.LogDebug("未注册 ICacheService，查询 {Query} 不走缓存", typeof(TRequest).Name);

            return await next().ConfigureAwait(false);
        }

        var entry = await cache.GetOrCreateAsync(
            cacheable.CacheKey,
            async token =>
            {
                var response = await next().ConfigureAwait(false);
                return response;
            },
            cacheable.CacheDuration,
            cancellationToken).ConfigureAwait(false);

        logger.LogDebug("查询 {Query} 已按缓存键 {Key} 处理", typeof(TRequest).Name, cacheable.CacheKey);

        return entry;
    }
}
