using Ledon.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 协议/套接字入口：
/// <code>
/// builder.Services.AddLedonTcpServer(o =&gt; o.Port = 9000, new LengthPrefixedFrameCodec());
/// builder.Services.AddLedonUdpEndpoint(o =&gt; o.Port = 9001);
/// </code>
/// 帧编解码（长度前缀/分隔符/定长）与包编解码（文本/结构化二进制）可自由组合。
/// </summary>
public static class LedonProtocolsServiceCollectionExtensions
{
    /// <summary>注册 TCP 服务端（帧编解码与处理器由调用方提供）。</summary>
    public static IServiceCollection AddLedonTcpServer(
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

    /// <summary>注册 UDP 端点。</summary>
    public static IServiceCollection AddLedonUdpEndpoint(
        this IServiceCollection services,
        Action<UdpEndpointOptions> configure,
        IUdpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(handler);

        var options = new UdpEndpointOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(handler);
        services.AddHostedService<UdpEndpoint>();

        return services;
    }
}
