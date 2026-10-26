using Ledon.Ids;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>ID 生成器注册。</summary>
public static class LedonIdsServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ID 生成器：
    /// <code>
    /// builder.Services.AddLedonIds(o =&gt; o.WorkerId = 1);
    /// </code>
    /// 多实例部署时每个实例的 WorkerId 必须唯一。
    /// </summary>
    public static IServiceCollection AddLedonIds(
        this IServiceCollection services,
        Action<LedonIdOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonIdOptions();
        configure?.Invoke(options);

        if (options.WorkerId is < 0 or > 1023)
            throw new InvalidOperationException("LedonIdOptions.WorkerId 必须在 0-1023 之间");

        services.TryAddSingleton(options);
        services.TryAddSingleton(_ => new SnowflakeIdGenerator(options.WorkerId, options.Epoch));
        services.TryAddSingleton<IIdGenerator, LedonIdGenerator>();

        return services;
    }
}
