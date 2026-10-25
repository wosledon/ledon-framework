using System.Text.Json;
using Ledon.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 分布式缓存入口。多实例部署（水平扩展）时替换进程内实现：
/// <code>
/// builder.Services.AddLedonDistributedCaching(o =&gt; o.RedisConnectionString = "localhost:6379");
/// </code>
/// 不配连接字符串时退化为进程内内存实现（开发/单机可用，语义与内存缓存一致）。
/// 与带 single-flight 的纯内存实现（AddLedonCaching）二选一；
/// 要同时要 L1+L2 两级用 <c>AddLedonHybridCaching</c>。
/// </summary>
public static class LedonDistributedCachingServiceCollectionExtensions
{
    public static IServiceCollection AddLedonDistributedCaching(
        this IServiceCollection services,
        Action<LedonDistributedCachingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonDistributedCachingOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            services.AddStackExchangeRedisCache(redis => redis.Configuration = options.RedisConnectionString);

            // 前缀删除要枚举 key（SCAN）——经 multiplexer 直连 Redis（惰性建连，不阻塞启动）
            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(options.RedisConnectionString!));
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.Replace(ServiceDescriptor.Singleton<ICacheService>(provider => new DistributedCacheService(
            provider.GetRequiredService<IDistributedCache>(),
            options,
            provider.GetService<IConnectionMultiplexer>(),
            provider.GetService<ICacheKeyPrefixProvider>(),
            provider.GetService<JsonSerializerOptions>())));

        return services;
    }
}
