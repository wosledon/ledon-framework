using Ledon.Protocols;
using Ledon.Sip.Server;
using Ledon.Sockets.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SIP 服务端入口（基于 <see cref="Ledon.Sockets.Server"/> 的 socket 库）：
/// <code>
/// builder.Services.AddLedonSipServer(o =&gt; o.Port = 5060);
/// builder.Services.AddSingleton&lt;ISipHandler, MySipHandler&gt;();
/// </code>
/// 传输（socket/作用域/并发）由 <c>Ledon.Sockets.Server</c> 负责；
/// 协议语义（解析/应答）由 <c>Ledon.Sip</c> 负责——两者通过适配器组合。
/// </summary>
public static class LedonSipServerServiceCollectionExtensions
{
    /// <summary>注册 SIP over UDP 服务端（默认 5060）。</summary>
    public static IServiceCollection AddLedonSipServer(this IServiceCollection services)
        => services.AddLedonSipServer(_ => { });

    /// <summary>注册 SIP over UDP 服务端并配置。</summary>
    public static IServiceCollection AddLedonSipServer(
        this IServiceCollection services,
        Action<SipServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SipServerOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IUdpHandler, SipUdpHandler>();

        // socket 服务端只负责收发：SIP 语义在 SipUdpHandler 里
        services.AddLedonSocketUdpServer<SipUdpHandler>(socket =>
        {
            socket.Address = options.Address;
            socket.Port = options.Port;
        });

        return services;
    }
}
