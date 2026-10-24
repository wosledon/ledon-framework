using Ledon.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 分布式缓存入口。多实例部署（水平扩展）时替换进程内实现：
/// <code>
/// builder.Services.AddLedonDistributedCaching(o =&gt; o.RedisConnectionString = "localhost:6379");
/// </code>
/// 不配连接字符串时退化为进程内内存实现（开发/单机可用，语义与内存缓存一致）。
/// 与 <see cref="AddLedonCaching"/> 二选一——后者是带 single-flight 的纯内存实现。
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
            services.AddStackExchangeRedisCache(redis => redis.Configuration = options.RedisConnectionString);
        else
            services.AddDistributedMemoryCache();

        services.Replace(ServiceDescriptor.Singleton<ICacheService, DistributedCacheService>());

        return services;
    }
}
