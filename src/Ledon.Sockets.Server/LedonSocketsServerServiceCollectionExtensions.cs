using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Ledon;
using Ledon.Module;
using Ledon.Protocols;
using Ledon.Sockets.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 协议服务端入口：
/// <code>
/// builder.Services.AddLedonSocketServer(o =&gt; o.Port = 9000, new LengthPrefixedFrameCodec());
/// builder.Services.AddLedonSocketUdpServer(o =&gt; o.Port = 9001);
/// </code>
/// 帧编解码（长度前缀/分隔符/定长）与包编解码（结构体映射）可自由组合。
/// </summary>
public static class LedonSocketsServerServiceCollectionExtensions
{
    /// <summary>注册 TCP 服务端（帧编解码与处理器由调用方提供）。</summary>
    public static IServiceCollection AddLedonSocketServer(
        this IServiceCollection services,
        Action<TcpServerOptions> configure,
        IFrameCodec codec,
        IConnectionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);

        var options = new TcpServerOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(codec);
        services.TryAddSingleton(handler);
        services.AddHostedService<TcpServer>();

        return services;
    }

    /// <summary>
    /// 注册 TCP 服务端，处理器由容器延迟解析（处理器依赖其它服务时用，
    /// 避免注册期构建 ServiceProvider）。
    /// </summary>
    public static IServiceCollection AddLedonSocketServer(
        this IServiceCollection services,
        Action<TcpServerOptions> configure,
        IFrameCodec codec,
        Func<IServiceProvider, IConnectionHandler> handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handlerFactory);

        var options = new TcpServerOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(codec);
        services.AddHostedService<TcpServer>(provider => new TcpServer(
            provider.GetRequiredService<TcpServerOptions>(),
            codec,
            handlerFactory(provider),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<TcpServer>>()));

        return services;
    }

    /// <summary>注册 UDP 服务端。</summary>
    public static IServiceCollection AddLedonSocketUdpServer(
        this IServiceCollection services,
        Action<UdpServerOptions> configure,
        IUdpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(handler);

        var options = new UdpServerOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(handler);
        services.AddHostedService<UdpServer>();

        return services;
    }

    /// <summary>注册 UDP 服务端（处理器由容器解析，避免注册期构建 ServiceProvider）。</summary>
    public static IServiceCollection AddLedonSocketUdpServer<THandler>(
        this IServiceCollection services,
        Action<UdpServerOptions> configure)
        where THandler : class, IUdpHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new UdpServerOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IUdpHandler, THandler>();
        services.AddHostedService<UdpServer>();

        return services;
    }

    /// <summary>
    /// 注册类型化分发（帧前 2 字节 = 类型标识，其余按 <see cref="IPacketCodec{TPacket}"/> 反序列化），
    /// 并扫描业务程序集里带 <see cref="PacketTypeAttribute"/> 的包与其 <see cref="IPacketHandler{TPacket}"/>。
    /// </summary>
    [RequiresDynamicCode("处理器注册基于反射扫描与编译委托；裁剪/AOT 场景请显式注册。")]
    public static IServiceCollection AddLedonPacketHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPacketDispatcher, TypeIdPacketDispatcher>();

        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(candidate => candidate is { IsClass: true, IsAbstract: false }
                    && candidate.GetCustomAttribute<PacketTypeAttribute>() is not null))
            {
                RegisterHandler(services, type);
            }
        }

        return services;
    }

    private static void RegisterHandler(IServiceCollection services, Type packetType)
    {
        var attribute = packetType.GetCustomAttribute<PacketTypeAttribute>()!;
        var handlerInterface = packetType.GetInterfaces().FirstOrDefault(candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IPacketHandler<>));

        if (handlerInterface is null)
            return;   // 只有 [PacketType] 没有处理器：等处理器出现时再注册

        var handlerType = handlerInterface.DeclaringType is null ? null : FindImplementation(packetType, handlerInterface);
        if (handlerType is null)
            return;

        services.TryAdd(new ServiceDescriptor(handlerInterface, handlerType, ServiceLifetime.Scoped));

        // 编译解码委托：启动时一次，之后零反射
        var serializerType = typeof(PacketSerializer<>).MakeGenericType(packetType);
        var readMethod = serializerType.GetMethod("Read", [typeof(ReadOnlySpan<byte>), typeof(bool)])!;
        var delegateType = typeof(Func<,,>).MakeGenericType(typeof(ReadOnlySpan<byte>), typeof(bool), packetType);
        var typed = readMethod.CreateDelegate(delegateType);
        var typeId = attribute.Id;

        PacketHandlerRegistry.Register(new PacketHandlerRegistration
        {
            HandlerType = handlerInterface,
            PacketType = packetType,
            TypeId = typeId,
            Decode = span => typed.DynamicInvoke(span, false),
            InvokeAsync = (handler, connection, packet, ct) =>
            {
                var method = handlerInterface.GetMethod("HandleAsync")!;

                return (Task)method.Invoke(handler, [connection, packet, ct])!;
            },
        });
    }

    private static Type? FindImplementation(Type packetType, Type handlerInterface)
    {
        // 处理器与包同名约定：LoginRequestHandler；也接受任意实现类（按容器解析）
        var implementations = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(candidate => candidate is { IsClass: true, IsAbstract: false }
                && handlerInterface.IsAssignableFrom(candidate))
            .ToArray();

        var byName = implementations.FirstOrDefault(candidate => candidate.Name == $"{packetType.Name}Handler");

        return byName ?? implementations.FirstOrDefault();
    }
}
