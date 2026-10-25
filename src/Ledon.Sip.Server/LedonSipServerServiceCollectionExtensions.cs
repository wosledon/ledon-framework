using Ledon.Protocols;
using Ledon.Sip;
using Ledon.Sip.Server;
using Ledon.Sockets.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SIP 服务端入口（基于 <see cref="Ledon.Sockets.Server"/> 的 socket 库）：
/// <code>
/// builder.Services.AddLedonSipServer(o =&gt; o.Port = 5060);
/// builder.Services.AddSingleton&lt;ISipHandler, MySipHandler&gt;();
/// </code>
/// 传输（socket/作用域/并发）由 <c>Ledon.Sockets.Server</c> 负责；
/// 协议语义（解析/应答/事务）由 <c>Ledon.Sip</c> 负责——两者通过适配器组合。
/// </summary>
public static class LedonSipServerServiceCollectionExtensions
{
    /// <summary>注册 SIP 服务端（UDP + 可选 TCP，默认 5060）。</summary>
    public static IServiceCollection AddLedonSipServer(this IServiceCollection services)
        => services.AddLedonSipServer(_ => { });

    /// <summary>注册 SIP 服务端并配置。</summary>
    public static IServiceCollection AddLedonSipServer(
        this IServiceCollection services,
        Action<SipServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SipServerOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<SipTransactionOptions>(_ => new SipTransactionOptions());

        // 事务层是单例：UDP 与 TCP 共用一套事务状态（同一 branch 只在一个传输上出现）；
        // 同时以 IHostedService 身份在宿主持停时清理过期事务
        services.TryAddSingleton<SipTransactionLayer>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SipTransactionLayer>());

        // socket 服务端只负责收发：SIP 语义在 SipUdpHandler / SipTcpHandler 里
        services.AddLedonSocketUdpServer<SipUdpHandler>(socket =>
        {
            socket.Address = options.Address;
            socket.Port = options.Port;
        });

        if (options.EnableTcp)
        {
            // TCP 上按 Content-Length 切帧；处理器与 UDP 共用同一套事务层
            services.TryAddSingleton<SipTcpHandler>();
            services.TryAddSingleton<IConnectionHandler>(provider => provider.GetRequiredService<SipTcpHandler>());

            services.AddLedonSocketServer(
                tcp =>
                {
                    tcp.Address = options.Address;
                    tcp.Port = options.EffectiveTcpPort;
                },
                new SipTcpFrameCodec(),
                provider => provider.GetRequiredService<SipTcpHandler>());
        }

        return services;
    }
}
