using System.Reflection;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Asp.Versioning.Routing;
using Ledon;
using Ledon.Module;
using Ledon.Module.Hosting;
using Ledon.Web.ApiConventions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

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
        {
            builder.Services.AddOpenApi(openApi =>
            {
                // 文档元数据：标题/描述（Scalar/Swagger UI 展示）
                openApi.AddDocumentTransformer((document, context, cancellationToken) =>
                {
                    document.Info.Title = options.OpenApiTitle ?? "Ledon API";
                    if (!string.IsNullOrWhiteSpace(options.OpenApiDescription))
                        document.Info.Description = options.OpenApiDescription;

                    return Task.CompletedTask;
                });

                // 安全方案：检测到认证包（Ledon.Security）在场时提供 Bearer 方案，
                // 带 [Authorize] 元数据的端点自动带上安全要求（文档 UI 的 Authorize 按钮）
                if (SecurityRegistered(builder.Services))
                {
                    openApi.AddDocumentTransformer((document, context, cancellationToken) =>
                    {
                        EnsureBearerScheme(document);
                        return Task.CompletedTask;
                    });

                    // 操作级变换：document.Components 可能在 schema 生成阶段被重建，
                    // 方案在操作变换里按需补齐（幂等），安全要求引用同一 document 才能序列化为 {"Bearer": []}
                    openApi.AddOperationTransformer((operation, context, cancellationToken) =>
                    {
                        var document = context.Document;
                        EnsureBearerScheme(document);

                        var requiresAuth = context.Description.ActionDescriptor?.EndpointMetadata
                            .Any(metadata => metadata is Microsoft.AspNetCore.Authorization.IAuthorizeData) == true;

                        if (!requiresAuth)
                            return Task.CompletedTask;

                        operation.Security ??= [];
                        operation.Security.Add(new Microsoft.OpenApi.OpenApiSecurityRequirement
                        {
                            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document, null)] = [],
                        });

                        return Task.CompletedTask;
                    });
                }
            });
        }

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

        // 限流（固定窗口）：分区键 = X-Api-Key（有则按调用方）或客户端 IP；超限 429 ProblemDetails
        if (options.UseRateLimiting)
        {
            builder.Services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    var partition = httpContext.Request.Headers.TryGetValue(RateLimiting.ApiKeyHeaderName, out var apiKey)
                        && !string.IsNullOrWhiteSpace(apiKey)
                            ? $"apikey:{apiKey}"
                            : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

                    return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.RateLimitPermitLimit,
                        Window = TimeSpan.FromSeconds(options.RateLimitWindowSeconds),
                        QueueLimit = 0,
                    });
                });

                limiter.OnRejected = async (context, cancellationToken) =>
                {
                    var problemDetails = context.HttpContext.RequestServices
                        .GetService<IProblemDetailsService>();

                    var problem = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "请求过于频繁",
                        Detail = $"限流：窗口 {options.RateLimitWindowSeconds} 秒内最多 {options.RateLimitPermitLimit} 次请求",
                        Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}",
                    };

                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                        problem.Extensions["retryAfter"] = retryAfter.TotalSeconds;

                    if (problemDetails is not null)
                    {
                        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        await problemDetails.TryWriteAsync(new ProblemDetailsContext
                        {
                            HttpContext = context.HttpContext,
                            ProblemDetails = problem,
                        });
                        return;
                    }

                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
                };
            });
        }

        // 响应压缩（Brotli 优先、Gzip 兜底；HTTPS 也启用）
        if (options.UseResponseCompression)
        {
            builder.Services.AddResponseCompression(compression =>
            {
                compression.EnableForHttps = true;
                compression.Providers.Add<BrotliCompressionProvider>();
                compression.Providers.Add<GzipCompressionProvider>();
            });
        }

        // 请求超时（默认 30s，超时 504）
        if (options.UseRequestTimeouts)
        {
            builder.Services.AddRequestTimeouts(timeouts =>
                timeouts.DefaultPolicy = new RequestTimeoutPolicy
                {
                    Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                    TimeoutStatusCode = 504,
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

    /// <summary>确保 Bearer 安全方案在场（幂等）。</summary>
    private static void EnsureBearerScheme(Microsoft.OpenApi.OpenApiDocument document)
    {
        document.Components ??= new Microsoft.OpenApi.OpenApiComponents();

        document.Components.SecuritySchemes ??=
            new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Name = "Authorization",
            Description = "JWT 访问令牌：Authorization: Bearer {token}",
        };
    }

    /// <summary>
    /// 认证包（Ledon.Security）是否在场：按实现类型所在程序集名检测。
    /// 不能用 <c>IAuthenticationSchemeProvider</c> 判据——MVC/授权自身也会注册它（误报）。
    /// </summary>
    private static bool SecurityRegistered(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ImplementationType?.Assembly.GetName().Name == "Ledon.Security");
}
