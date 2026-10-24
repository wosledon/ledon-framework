using Ledon;
using Ledon.Module;
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
        Action<LedonWebOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 模块系统零配置接入（自动发现/注入/生命周期）
        builder.Services.AddLedon();

        var options = new LedonWebOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        var assemblies = LedonAssemblies.Scan();

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

        // OpenAPI 按需开启
        if (options.UseOpenApi)
            builder.Services.AddOpenApi();

        return builder;
    }
}
