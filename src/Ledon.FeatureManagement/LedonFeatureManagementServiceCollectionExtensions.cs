using Ledon.FeatureManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 功能开关入口：
/// <code>
/// builder.Services.AddLedonFeatureManagement();   // 读 Features 配置段，支持配置中心热更新
/// builder.Services.AddLedonFeatureManagement(o =&gt; o.DefaultEnabled = false);
/// </code>
/// </summary>
public static class LedonFeatureManagementServiceCollectionExtensions
{
    /// <summary>注册功能开关服务。</summary>
    public static IServiceCollection AddLedonFeatureManagement(this IServiceCollection services)
        => services.AddLedonFeatureManagement(_ => { });

    /// <summary>注册功能开关服务并配置选项。</summary>
    public static IServiceCollection AddLedonFeatureManagement(
        this IServiceCollection services,
        Action<LedonFeatureManagementOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonFeatureManagementOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ConfigurationFeatureStore>();
        services.TryAddSingleton<IFeatureManager, FeatureManager>();

        return services;
    }

    /// <summary>把配置段的开关变化接到 <see cref="IConfiguration"/> 重载令牌上（热更新已内置，此方法便于测试注入）。</summary>
    public static IServiceCollection AddLedonFeatureManagement(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<LedonFeatureManagementOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration);
        return services.AddLedonFeatureManagement(configure ?? (_ => { }));
    }
}
