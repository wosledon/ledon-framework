using Ledon.Caching;
using Ledon.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 多租户入口。日常用法：
/// <code>
/// builder.Services.AddLedonMultiTenancy();
/// app.UseLedonMultiTenancy();   // 解析 X-Tenant-Id
/// </code>
/// 业务代码注入 <see cref="ITenantContext"/> 取当前租户；数据隔离按需在查询层应用
/// （v1 提供上下文与解析，不过滤查询——约定显式，避免隐式过滤的意外）。
/// </summary>
public static class LedonMultiTenancyServiceCollectionExtensions
{
    public static IServiceCollection AddLedonMultiTenancy(
        this IServiceCollection services,
        Action<MultiTenancyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MultiTenancyOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ITenantContext, TenantContext>();

        // 缓存键按租户隔离（多租户 + 缓存同场时的防泄漏默认值；TryAdd：可自行替换）
        if (!string.IsNullOrEmpty(options.CacheKeyPrefix))
            services.TryAddSingleton<ICacheKeyPrefixProvider, TenantCacheKeyPrefixProvider>();

        return services;
    }
}

/// <summary>租户解析中间件装配。</summary>
public static class LedonMultiTenancyApplicationExtensions
{
    public static IApplicationBuilder UseLedonMultiTenancy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<TenantResolutionMiddleware>();
        return app;
    }
}
