using Ledon.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 混合两级缓存入口（L1 进程内 + L2 分布式，.NET 10 HybridCache）：
/// <code>
/// // L2 注册了 IDistributedCache（Redis）即自动启用两级；否则退化为纯 L1
/// builder.Services.AddLedonDistributedCaching(o =&gt; o.RedisConnectionString = "localhost:6379");
/// builder.Services.AddLedonHybridCaching();
/// </code>
/// 适合读多写少的热数据：L1 命中零网络往返，跨实例一致性由 L2 保证。
/// </summary>
public static class LedonHybridCachingServiceCollectionExtensions
{
    public static IServiceCollection AddLedonHybridCaching(
        this IServiceCollection services,
        Action<LedonHybridCachingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonHybridCachingOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.AddHybridCache();
        services.Replace(ServiceDescriptor.Singleton<ICacheService, HybridCacheService>());

        return services;
    }
}
