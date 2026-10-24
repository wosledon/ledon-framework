using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Ledon;
using Ledon.Module;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 自动发现的 Hub 类型集合（类型化注册：避免与其它功能包的 <c>Type[]</c> 注册互相覆盖）。
/// </summary>
public sealed class DiscoveredHubs(IReadOnlyList<Type> hubs)
{
    /// <summary>已发现的 Hub 实现类型（按类型全名排序）。</summary>
    public IReadOnlyList<Type> Hubs { get; } = hubs;
}

/// <summary>SignalR 配置。</summary>
public sealed class LedonSignalROptions
{
    /// <summary>
    /// Redis 连接串（多实例部署）。配置后自动挂 Redis backplane：
    /// 发往任一实例的消息经 Redis 广播给所有实例的客户端（不配则只有本实例的连接能收到）。
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>backplane 的 Redis 频道前缀（多套 SignalR 应用共享 Redis 时区分），默认 ledon:signalr:。</summary>
    public string RedisChannelPrefix { get; set; } = "ledon:signalr:";
}

/// <summary>
/// SignalR 集成：Hub 自动发现 + 按约定路径映射（/hubs/{hub 名去 Hub 后缀，kebab-case）。
/// <code>
/// builder.Services.AddLedonSignalR();
/// app.MapLedonSignalRHubs();   // ChatHub → /hubs/chat
///
/// // 多实例部署：挂 Redis backplane（跨实例广播）
/// builder.Services.AddLedonSignalR(o =&gt; o.RedisConnectionString = "localhost:6379");
/// </code>
/// Hub 类照常写在业务程序集里（依赖注入、授权特性、模块事件均可用）。
/// </summary>
public static class LedonSignalRServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的 Hub 自动发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，Hub 类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    /// <summary>注册 SignalR 并发现被扫描程序集中的 <see cref="Hub"/> 子类。</summary>
    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonSignalR(this IServiceCollection services) =>
        services.AddLedonSignalR(null);

    /// <param name="services">服务集合。</param>
    /// <param name="configure">SignalR 配置（如多实例 backplane 的 Redis 连接串）。</param>
    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonSignalR(
        this IServiceCollection services,
        Action<LedonSignalROptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonSignalROptions();
        configure?.Invoke(options);

        var signalR = services.AddSignalR();

        // 多实例部署：Redis backplane（跨实例广播；连接惰性建立，Redis 短暂不在线不阻塞启动）
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            signalR.AddStackExchangeRedis(
                options.RedisConnectionString!,
                redis => redis.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal(options.RedisChannelPrefix));
        }

        // Hub 自动发现（沿用框架的允许列表扫描；Hub<T> 强类型 Hub 同样匹配）
        var hubs = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(Hub).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        services.AddSingleton(new DiscoveredHubs(hubs));   // 供 MapLedonSignalRHubs 映射

        return services;
    }

    /// <summary>
    /// 映射全部已发现的 Hub：路径 = /hubs/{类型名去 "Hub" 后缀，kebab-case}
    /// （ChatHub → /hubs/chat；无后缀的类名原样 kebab）。
    /// </summary>
    /// <param name="endpoints">端点路由构建器。</param>
    [RequiresDynamicCode(AotBoundary)]
    public static IEndpointRouteBuilder MapLedonSignalRHubs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var hubs = endpoints.ServiceProvider.GetService<DiscoveredHubs>()?.Hubs ?? [];
        if (hubs.Count == 0)
            return endpoints;

        // MapHub 只有泛型重载——发现的 Hub 类型是运行时的，需反射调用
        var mapHub = typeof(HubEndpointRouteBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(method => method is
            {
                Name: nameof(HubEndpointRouteBuilderExtensions.MapHub),
                IsGenericMethodDefinition: true,
            } && method.GetParameters().Length == 2)
            ?? throw new InvalidOperationException("未找到 HubEndpointRouteBuilderExtensions.MapHub<T> 重载。");

        foreach (var hub in hubs)
        {
            // 幂等：已映射过的 Hub 跳过（重复调用/多处装配时不炸）
            var alreadyMapped = endpoints.DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .Any(endpoint => endpoint.Metadata.GetMetadata<HubMetadata>()?.HubType == hub);

            if (alreadyMapped)
                continue;

            var closed = mapHub.MakeGenericMethod(hub);
            closed.Invoke(null, [endpoints, $"/hubs/{ToRouteName(hub.Name)}"]);
        }

        return endpoints;
    }

    /// <summary>ChatHub → chat；NotificationHub → notification。</summary>
    private static string ToRouteName(string typeName)
    {
        var name = typeName.EndsWith("Hub", StringComparison.Ordinal)
            ? typeName[..^"Hub".Length]
            : typeName;

        if (name.Length == 0)
            name = typeName;

        return string.Concat(name.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? "-" + character.ToString().ToLowerInvariant()
                : character.ToString().ToLowerInvariant()));
    }
}
