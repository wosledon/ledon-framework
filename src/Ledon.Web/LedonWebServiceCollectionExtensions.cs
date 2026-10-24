using Ledon;
using Ledon.Module;
using Ledon.Module.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Web;

/// <summary>
/// Web 入口扩展。日常用法两行：
/// <code>
/// builder.AddLedonWeb();
/// app.UseLedonWeb();
/// </code>
/// </summary>
public static class LedonWebServiceCollectionExtensions
{
    public static WebApplicationBuilder AddLedonWeb(
        this WebApplicationBuilder builder,
        Action<LedonWebOptions>? configure = null) =>
        builder.AddLedonWeb(configure, null);

    /// <param name="configure">Web 功能开关（控制器/OpenAPI）。</param>
    /// <param name="configureModules">转发给模块系统的逃生门配置（默认自动发现）。</param>
    public static WebApplicationBuilder AddLedonWeb(
        this WebApplicationBuilder builder,
        Action<LedonWebOptions>? configure,
        Action<LedonOptions>? configureModules)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 模块系统零配置接入（自动发现/注入/生命周期）
        builder.Services.AddLedon(configureModules);

        var options = new LedonWebOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        var assemblies = LedonAssemblies.Scan();

        // OpenAPI 按需开启（先于 MVC 注册）
        if (options.UseOpenApi)
            builder.Services.AddOpenApi();

        // 控制器自动发现：业务程序集挂为 MVC ApplicationPart
        if (options.UseControllers)
        {
            var mvc = builder.Services.AddControllers();
            foreach (var assembly in assemblies)
                mvc.AddApplicationPart(assembly);
        }

        // 端点自动注册：IEndpoint 实现类进容器（单例，启动时统一映射一次）
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IEndpoint).IsAssignableFrom(type)))
            {
                builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IEndpoint), type));
            }
        }

        return builder;
    }
}
