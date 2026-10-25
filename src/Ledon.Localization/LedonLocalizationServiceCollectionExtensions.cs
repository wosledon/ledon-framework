using Ledon.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 本地化入口（请求文化 + 文案）：
/// <code>
/// builder.Services.AddLedonLocalization(o =&gt;
/// {
///     o.DefaultCulture = "zh-CN";
///     o.SupportedCultures.Add("en-US");
/// });
/// app.UseLedonRequestLocalization();   // 路由之前
/// </code>
/// </summary>
public static class LedonLocalizationServiceCollectionExtensions
{
    /// <summary>注册本地化服务。</summary>
    public static IServiceCollection AddLedonLocalization(this IServiceCollection services)
        => services.AddLedonLocalization(_ => { });

    /// <summary>注册本地化服务并配置。</summary>
    public static IServiceCollection AddLedonLocalization(
        this IServiceCollection services,
        Action<LedonLocalizationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonLocalizationOptions();
        configure(options);

        if (options.SupportedCultures.Count == 0)
            throw new InvalidOperationException("至少需要一个受支持的文化");

        // 默认文化必须在支持列表里（否则解析出来也用不了）
        options.SupportedCultures.Add(options.DefaultCulture);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ICultureResolver, DefaultCultureResolver>();
        services.TryAddSingleton<ILocalizationStore>(provider =>
            new JsonFileLocalizationStore(
                System.IO.Path.Combine(AppContext.BaseDirectory, "Resources"),
                options.SupportedCultures));

        return services;
    }

    /// <summary>改用内存/自定义文案源（JSON 文件之外的场景）。</summary>
    public static IServiceCollection AddLedonLocalizationStore<TStore>(this IServiceCollection services)
        where TStore : class, ILocalizationStore
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ILocalizationStore>();
        services.TryAddSingleton<ILocalizationStore, TStore>();

        return services;
    }
}
