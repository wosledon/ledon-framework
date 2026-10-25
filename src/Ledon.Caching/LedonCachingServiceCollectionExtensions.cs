using Ledon.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 缓存入口扩展。日常用法一行：
/// <code>builder.Services.AddLedonCaching();</code>
/// </summary>
public static class LedonCachingServiceCollectionExtensions
{
    public static IServiceCollection AddLedonCaching(
        this IServiceCollection services,
        Action<LedonCachingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonCachingOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // 跨实例失效总线：默认进程内（单实例零依赖）；开启后走 Redis 发布订阅
        services.TryAddSingleton<ICacheInvalidationBus>(provider =>
        {
            var resolved = provider.GetRequiredService<LedonCachingOptions>();

            if (resolved.CrossInstanceInvalidation && string.IsNullOrWhiteSpace(resolved.RedisConnectionString))
                throw new InvalidOperationException("开启 CrossInstanceInvalidation 需要配置 RedisConnectionString");

            return resolved.CrossInstanceInvalidation
                ? new RedisCacheInvalidationBus(resolved)
                : new InProcessCacheInvalidationBus();
        });

        services.TryAddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));
        services.TryAddSingleton<ICacheService, MemoryCacheService>();

        return services;
    }
}
