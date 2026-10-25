using Ledon.Sip.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SIP 客户端入口（基于 <see cref="Ledon.Sockets.Client"/> 的 socket 库）：
/// <code>
/// builder.Services.AddLedonSipClient(o =&gt; { o.Host = "pbx.example.com"; o.Port = 5060; });
///
/// var client = provider.GetRequiredService&lt;SipClient&gt;();
/// await client.ConnectAsync();
/// var response = await client.SendRequestAsync(SipParser.CreateRequest("OPTIONS", "sip:pbx.example.com", headers));
/// </code>
/// </summary>
public static class LedonSipClientServiceCollectionExtensions
{
    /// <summary>注册 SIP 客户端。</summary>
    public static IServiceCollection AddLedonSipClient(this IServiceCollection services)
        => services.AddLedonSipClient(_ => { });

    /// <summary>注册 SIP 客户端并配置。</summary>
    public static IServiceCollection AddLedonSipClient(
        this IServiceCollection services,
        Action<SipClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SipClientOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<SipClient>();

        return services;
    }
}
