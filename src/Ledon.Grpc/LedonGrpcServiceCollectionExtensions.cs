using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Grpc.AspNetCore.Server;
using Grpc.Health.V1;
using Grpc.HealthCheck;
using Ledon.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>gRPC 服务端/客户端配置。</summary>
public sealed class LedonGrpcOptions
{
    /// <summary>
    /// 注册标准健康检查服务 <c>grpc.health.v1.Health</c>（默认开）：
    /// 随 <c>MapLedonGrpcServices</c> 一起映射，负载均衡/K8s gRPC 探针可直接用。
    /// </summary>
    public bool UseHealthChecks { get; set; } = true;

    /// <summary>
    /// 注册默认日志拦截器（默认开）：记录每次调用的方法、耗时与成败——
    /// gRPC 调用不再只在链路里可见，应用日志同样可观测。
    /// </summary>
    public bool UseLoggingInterceptor { get; set; } = true;

    /// <summary>
    /// 注册异常映射拦截器（默认开）：业务异常转成对应的 gRPC 状态码
    /// （ArgumentException → InvalidArgument、KeyNotFound → NotFound……），
    /// 而不是一律 Unknown + 堆栈。自定义映射实现 <see cref="IGrpcExceptionMapper"/>
    /// 并注册进容器即可。
    /// </summary>
    public bool UseErrorInterceptor { get; set; } = true;

    /// <summary>
    /// 客户端没传 deadline 时的兜底上限，默认 30 秒。
    /// 没有它，一个不观察取消令牌的处理器会把连接永久占住。
    /// </summary>
    public static TimeSpan DefaultDeadline { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// gRPC 集成：服务自动发现 + 按约定映射（与 Ledon 其它包一致的"引用框架 = 选择加入"）。
/// <code>
/// // 服务端：业务程序集里的 gRPC 服务实现类自动映射（无需逐个 MapGrpcService）
/// builder.Services.AddLedonGrpc();
/// app.MapLedonGrpcServices();
///
/// // 客户端：强类型客户端（地址配置驱动，可叠加 Ledon.Resilience 的弹性策略）
/// builder.Services.AddLedonGrpcClient&lt;Greeter.GreeterClient&gt;("https://localhost:7001");
/// </code>
/// </summary>
public static class LedonGrpcServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的 gRPC 服务发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，服务实现类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    /// <summary>
    /// 注册 gRPC 服务端并发现被扫描程序集中的 gRPC 服务实现类
    /// （protoc 生成的抽象基类 + 业务实现类形态）。
    /// </summary>
    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonGrpc(
        this IServiceCollection services,
        Action<LedonGrpcOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonGrpcOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        var discovered = GrpcServiceDiscovery.Discover().ToList();

        if (options.UseHealthChecks)
        {
            // grpc.health.v1 标准健康服务：显式加入发现列表，随 MapLedonGrpcServices 映射。
            // 默认整体状态置为 SERVING（进程在跑即服务中；不置则空服务名返回 NotFound）
            services.TryAddSingleton(_ =>
            {
                var impl = new HealthServiceImpl();
                impl.SetStatus(string.Empty, Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving);
                return impl;
            });
            discovered.Add(typeof(HealthServiceImpl));
        }

        // 异常映射表：在 AddGrpc 之外注册（options lambda 是延迟执行的，
        // 写在里面会导致解析 IGrpcExceptionMapper 时还没注册）
        if (options.UseErrorInterceptor)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IGrpcExceptionMapper, DefaultGrpcExceptionMapper>());

        var grpc = services.AddGrpc(o =>
        {
            if (options.UseLoggingInterceptor)
                o.Interceptors.Add<LedonGrpcLoggingInterceptor>();

            if (options.UseErrorInterceptor)
                o.Interceptors.Add<LedonGrpcErrorInterceptor>();
        });

        services.AddSingleton(new DiscoveredGrpcServices(discovered));

        return services;
    }

    /// <summary>
    /// 注册强类型 gRPC 客户端（gRPC channel + 客户端工厂，地址配置驱动）。
    /// <para>
    /// 注意：<c>Ledon.Resilience</c> 的策略挂在 HttpClient 管道上（只认
    /// <see cref="System.Net.Http.HttpRequestException"/> 与 5xx），**不会**自动套到
    /// gRPC 调用上——gRPC 失败抛的是 <c>RpcException</c>。需要重试/熔断时请自行配置
    /// <c>GrpcClientFactoryOptions</c> 或按 <c>RpcException.Status.StatusCode</c> 自建策略。
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <typeparam name="TClient">protoc 生成的客户端类（如 <c>Greeter.GreeterClient</c>）。</typeparam>
    /// <param name="address">服务端地址（如 https://localhost:7001）。</param>
    /// <param name="configure">额外配置（请求头、凭据等）。</param>
    public static IServiceCollection AddLedonGrpcClient<TClient>(
        this IServiceCollection services,
        string address,
        Action<Grpc.Net.ClientFactory.GrpcClientFactoryOptions>? configure = null)
        where TClient : class
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("AddLedonGrpcClient: 必须配置服务端地址");

        services.AddGrpcClient<TClient>(options =>
        {
            options.Address = new Uri(address);
            configure?.Invoke(options);
        });

        return services;
    }
}

/// <summary>
/// 自动发现的 gRPC 服务实现类型集合（类型化注册：避免与其它功能包的 <c>Type[]</c> 注册互相覆盖）。
/// </summary>
public sealed class DiscoveredGrpcServices(IReadOnlyList<Type> services)
{
    /// <summary>已发现的 gRPC 服务实现类型（按类型全名排序）。</summary>
    public IReadOnlyList<Type> Services { get; } = services;
}

/// <summary>gRPC 端点映射扩展。</summary>
public static class LedonGrpcEndpointExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的 gRPC 服务发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，服务实现类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    /// <summary>
    /// 映射全部已发现的 gRPC 服务（<c>MapGrpcService&lt;T&gt;</c> 只有泛型重载，
    /// 发现的类型是运行时的，需反射调用）。幂等：重复调用/多处装配时不重复映射。
    /// </summary>
    [RequiresDynamicCode(AotBoundary)]
    public static IEndpointRouteBuilder MapLedonGrpcServices(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceTypes = endpoints.ServiceProvider.GetService<DiscoveredGrpcServices>()?.Services ?? [];
        if (serviceTypes.Count == 0)
            return endpoints;

        var mapService = typeof(GrpcEndpointRouteBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(method => method is
            {
                Name: nameof(GrpcEndpointRouteBuilderExtensions.MapGrpcService),
                IsGenericMethodDefinition: true,
            } && method.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("未找到 GrpcEndpointRouteBuilderExtensions.MapGrpcService<T> 重载。");

        foreach (var serviceType in serviceTypes)
        {
            // 幂等：已映射过的服务类型跳过
            var alreadyMapped = endpoints.DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .Any(endpoint => endpoint.Metadata.GetMetadata<GrpcMethodMetadata>()?.ServiceType == serviceType);

            if (alreadyMapped)
                continue;

            mapService.MakeGenericMethod(serviceType).Invoke(null, [endpoints]);
        }

        return endpoints;
    }
}
