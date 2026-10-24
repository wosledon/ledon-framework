using Asp.Versioning;
using Asp.Versioning.Routing;
using Ledon;
using Ledon.Module;
using Ledon.Module.Hosting;
using Ledon.Web.ApiConventions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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

    /// <param name="builder">Web 宿主构建器。</param>
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

        // apiVersion 路由约束无条件注册：扫描到的控制器带 {version:apiVersion} 路由时，
        // 即使没开启版本化也不会在路由构建阶段炸掉（约束映射缺失会 500）
        builder.Services.Configure<RouteOptions>(options =>
            options.ConstraintMap["apiVersion"] = typeof(ApiVersionRouteConstraint));

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

            // 模型验证失败统一 400 + ValidationProblemDetails（RFC 7807 格式）
            builder.Services.Configure<ApiBehaviorOptions>(behavior =>
                behavior.InvalidModelStateResponseFactory = context =>
                    new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState)));

            foreach (var assembly in assemblies)
                mvc.AddApplicationPart(assembly);

            // 统一响应包装（可选）
            if (options.UseUnifiedResponse)
            {
                builder.Services.Configure<MvcOptions>(mvcOptions =>
                    mvcOptions.Filters.Add<ApiResponseWrapperFilter>());
            }
        }

        // 全局异常处理（ProblemDetails：含关联 ID 与 TraceId）
        if (options.UseProblemDetails)
        {
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<LedonExceptionHandler>();
        }

        // API 版本化（URL 段 api/v{version}/...）
        if (options.UseApiVersioning)
        {
            builder.Services.AddApiVersioning(versioning =>
            {
                versioning.DefaultApiVersion = new ApiVersion(1, 0);
                versioning.AssumeDefaultVersionWhenUnspecified = true;
                versioning.ApiVersionReader = new UrlSegmentApiVersionReader();
            }).AddMvc();
        }

        // 跨域（CORS）：按配置的来源注册默认策略（空来源 = 允许任意，仅建议开发环境）
        if (options.UseCors)
        {
            builder.Services.AddCors(cors =>
            {
                if (options.CorsOrigins.Length > 0)
                {
                    cors.AddPolicy(options.CorsPolicyName, policy => policy
                        .WithOrigins(options.CorsOrigins)
                        .AllowAnyHeader()
                        .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS"));
                }
                else
                {
                    cors.AddPolicy(options.CorsPolicyName, policy => policy
                        .AllowAnyOrigin()
                        .AllowAnyHeader()
                        .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS"));
                }
            });
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
