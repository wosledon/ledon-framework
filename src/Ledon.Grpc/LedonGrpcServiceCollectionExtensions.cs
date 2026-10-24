using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Grpc.AspNetCore.Server;
using Ledon.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.Extensions.DependencyInjection;

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
    public static IServiceCollection AddLedonGrpc(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddGrpc();

        var serviceTypes = GrpcServiceDiscovery.Discover().ToArray();   // Type[] 注册（MapLedonGrpcServices 按 Type[] 解析）
        services.AddSingleton(serviceTypes);

        return services;
    }

    /// <summary>
    /// 注册强类型 gRPC 客户端（gRPC channel + 客户端工厂，地址配置驱动）。
    /// 已注册 <c>Ledon.Resilience</c> 时自动获得重试/超时/熔断。
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

        var serviceTypes = endpoints.ServiceProvider.GetService<Type[]>() ?? [];
        if (serviceTypes.Length == 0)
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
