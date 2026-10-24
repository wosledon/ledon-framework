using Asp.Versioning;
using Asp.Versioning.Routing;
using Ledon;
using Ledon.Module;
using Ledon.Module.Hosting;
using Ledon.Web.ApiConventions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using System.Reflection;

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

        // Data Protection 密钥环持久化到 Redis（多实例共享码环：cookie/防伪令牌跨实例可解密）
        if (!string.IsNullOrWhiteSpace(options.DataProtectionRedisConnectionString))
        {
            var applicationName = options.DataProtectionApplicationName
                ?? Assembly.GetEntryAssembly()?.GetName().Name
                ?? "ledon-app";

            builder.Services.AddDataProtection();

            // Configure 必须在 AddDataProtection 之后注册：DP 自带的 setup 会把
            // ApplicationDiscriminator 默认置为内容根路径，后注册的 Configure 才生效
            builder.Services.Configure<DataProtectionOptions>(
                dataProtection => dataProtection.ApplicationDiscriminator = applicationName);

            // AbortOnConnectFail=false + 惰性建连：Redis 短暂不在线不阻塞启动（后台重连），
            // 码环首次读写密钥时才真正连接；holder 随容器释放
            var redis = ConfigurationOptions.Parse(options.DataProtectionRedisConnectionString);
            redis.AbortOnConnectFail = false;

            var keyRing = new RedisKeyRingHolder(redis);
            builder.Services.AddSingleton(keyRing);
            builder.Services.AddDataProtection()
                .PersistKeysToStackExchangeRedis(() => keyRing.Database, "DataProtection-Keys");
        }

        var assemblies = LedonAssemblies.Scan();

        // Scalar UI 依赖 OpenAPI 文档：忘开 UseOpenApi 时启动即失败，而不是 UI 打开一片空白
        if (options.UseScalarUi && !options.UseOpenApi)
            throw new InvalidOperationException(
                "AddLedonWeb: UseScalarUi 需要同时开启 UseOpenApi（Scalar UI 是 OpenAPI 文档的可视化）");

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
        if (options.UseCors)        {
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
