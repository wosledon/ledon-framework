using Ledon.Locking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 分布式锁入口：
/// <code>
/// builder.Services.AddLedonLocking();                                  // 进程内（单实例）
/// builder.Services.AddLedonLocking(o =&gt; o.Provider = LedonLockProvider.Redis,
///     o =&gt; o.RedisConnectionString = "localhost:6379");               // 多实例
/// </code>
/// 业务只依赖 <see cref="IDistributedLock"/>，换提供程序不改代码。
/// </summary>
public static class LedonLockingServiceCollectionExtensions
{
    /// <summary>注册锁服务（单例）。</summary>
    public static IServiceCollection AddLedonLocking(this IServiceCollection services)
        => services.AddLedonLocking(_ => { }, _ => { });

    /// <summary>注册锁服务并配置提供程序。</summary>
    public static IServiceCollection AddLedonLocking(
        this IServiceCollection services,
        Action<LedonLockingOptions> configureProvider,
        Action<LedonLockingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureProvider);

        var options = new LedonLockingOptions();
        configureProvider(options);
        configure?.Invoke(options);

        if (options.Provider == LedonLockProvider.Redis && string.IsNullOrWhiteSpace(options.RedisConnectionString))
            throw new InvalidOperationException("使用 Redis 锁需要配置 RedisConnectionString");

        services.TryAddSingleton(options);

        services.TryAddSingleton<IDistributedLock>(provider =>
        {
            var resolved = provider.GetRequiredService<LedonLockingOptions>();

            return resolved.Provider switch
            {
                LedonLockProvider.Redis => new RedisDistributedLock(resolved),
                _ => new InMemoryDistributedLock(resolved),
            };
        });

        return services;
    }
}
