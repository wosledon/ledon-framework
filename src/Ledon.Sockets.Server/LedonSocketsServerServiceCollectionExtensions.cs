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

    /// <summary>注册类型化分发（帧前 2 字节 = 类型标识，其余按 <see cref="IPacketCodec{TPacket}"/> 反序列化），</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="bigEndian">
    /// 包的全局字节序，默认小端（与 <see cref="PacketSerializer{T}.Read(ReadOnlySpan{byte}, bool)"/> 一致）。
    /// 字段上标了 <c>[PacketField(BigEndian = true)]</c> 的仍然按字段自己的来。
    /// </param>
    /// <remarks>
    /// 处理器按约定扫描自动注册：带 <see cref="PacketTypeAttribute"/> 的包（类或结构体）
    /// + 实现 <c>IPacketHandler&lt;该包&gt;</c> 的处理器类。
    /// </remarks>
    [RequiresDynamicCode("处理器注册基于反射扫描与编译委托；裁剪/AOT 场景请显式注册。")]
    public static IServiceCollection AddLedonPacketHandlers(
        this IServiceCollection services,
        bool bigEndian = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPacketDispatcher, TypeIdPacketDispatcher>();

        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                // 结构体也是合法的包类型（[PacketType] 的 AttributeUsage 明确允许 Struct），
                // 只判 IsClass 会让所有 struct 包被静默跳过
                .Where(candidate => candidate is { IsAbstract: false, IsInterface: false }
                    && (candidate.IsClass || candidate.IsValueType)
                    && candidate.GetCustomAttribute<PacketTypeAttribute>() is not null))
            {
                RegisterHandler(services, type, bigEndian);
            }
        }

        return services;
    }

    private static void RegisterHandler(IServiceCollection services, Type packetType, bool bigEndian)
    {
        var attribute = packetType.GetCustomAttribute<PacketTypeAttribute>()!;

        // 处理器接口是 IPacketHandler<该包类型>：实现在**处理器类**上，不在包类型上
        var handlerInterface = typeof(IPacketHandler<>).MakeGenericType(packetType);

        var implementations = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(candidate => candidate is { IsClass: true, IsAbstract: false }
                && handlerInterface.IsAssignableFrom(candidate))
            .ToArray();

        if (implementations.Length == 0)
            return;   // 只有 [PacketType] 没有处理器：等处理器出现时再注册

        // 同名约定优先（LoginRequestHandler），否则取第一个实现
        var handlerType = implementations.FirstOrDefault(candidate => candidate.Name == $"{packetType.Name}Handler")
            ?? implementations[0];

        services.TryAdd(new ServiceDescriptor(handlerInterface, handlerType, ServiceLifetime.Scoped));

        // 编译解码委托：启动时一次，之后零反射。
        // 经泛型助手把 ReadOnlyMemory 转成 ReadOnlySpan 再调强类型委托——
        // 直接 DynamicInvoke(span) 会失败（ref struct 不能装箱）
        var readMethod = typeof(PacketSerializer<>).MakeGenericType(packetType)
            .GetMethod("Read", [typeof(ReadOnlySpan<byte>), typeof(bool)])!;

        var decoder = (Func<ReadOnlyMemory<byte>, object?>)typeof(LedonSocketsServerServiceCollectionExtensions)
            .GetMethod(nameof(CreateDecoder), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(packetType)
            .Invoke(null, [readMethod, bigEndian])!;

        var typeId = attribute.Id;

        PacketHandlerRegistry.Register(new PacketHandlerRegistration
        {
            HandlerType = handlerInterface,
            PacketType = packetType,
            TypeId = typeId,
            Decode = decoder,
            InvokeAsync = (handler, connection, packet, ct) =>
            {
                var method = handlerInterface.GetMethod("HandleAsync")!;

                return (Task)method.Invoke(handler, [connection, packet, ct])!;
            },
        });
    }

    /// <summary>把反射拿到的 Read 方法包成"吃 ReadOnlyMemory"的强类型委托。</summary>
    private static Func<ReadOnlyMemory<byte>, object?> CreateDecoder<TPacket>(MethodInfo readMethod, bool bigEndian)
    {
        var typed = (Func<ReadOnlySpan<byte>, bool, TPacket>)readMethod
            .CreateDelegate(typeof(Func<ReadOnlySpan<byte>, bool, TPacket>));

        return memory => typed(memory.Span, bigEndian);
    }
}
