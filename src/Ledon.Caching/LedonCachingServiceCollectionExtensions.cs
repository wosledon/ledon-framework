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

        services.TryAddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));
        services.TryAddSingleton<ICacheService, MemoryCacheService>();

        return services;
    }
}
