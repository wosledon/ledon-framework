using Ledon.Protocols;
using Ledon.Protocols.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 协议客户端入口：
/// <code>
/// builder.Services.AddLedonProtocolClient(o =&gt;
/// {
///     o.Host = "127.0.0.1";
///     o.Port = 9000;
///     o.AutoReconnect = true;
/// }, new LengthPrefixedFrameCodec());
/// </code>
/// 客户端随宿主启动自动连接，断线自动重连，收到的帧分发给 <see cref="IClientHandler"/>。
/// </summary>
public static class LedonProtocolsClientServiceCollectionExtensions
{
    /// <summary>注册 TCP 客户端（含托管连接循环）。</summary>
    public static IServiceCollection AddLedonProtocolClient(
        this IServiceCollection services,
        Action<ProtocolClientOptions> configure,
        IFrameCodec codec,
        IClientHandler handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);

        var options = new ProtocolClientOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(codec);
        services.TryAddSingleton(handler);
        services.AddHostedService<TcpClientHostedService>();

        return services;
    }

    /// <summary>注册 UDP 客户端。</summary>
    public static IServiceCollection AddLedonProtocolUdpClient(
        this IServiceCollection services,
        Action<ProtocolClientOptions> configure,
        IPacketCodec<object> codec,
        IClientHandler handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);

        var options = new ProtocolClientOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(codec);
        services.TryAddSingleton(handler);
        services.AddHostedService<UdpClientHostedService>();

        return services;
    }
}

/// <summary>把 TCP 客户端接进宿主生命周期（启动即连，停机即断）。</summary>
internal sealed class TcpClientHostedService(
    ProtocolClientOptions options,
    IFrameCodec codec,
    IClientHandler handler,
    Microsoft.Extensions.Logging.ILogger<TcpProtocolClient> logger)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = new TcpProtocolClient(options, codec, handler, logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.ConnectAsync(stoppingToken).ConfigureAwait(false);

                // 连接保持到断开或停机
                while (!stoppingToken.IsCancellationRequested && client.IsConnected)
                    await Task.Delay(200, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 连接失败：按间隔重试（AutoReconnect = false 时退出）
                if (!options.AutoReconnect)
                    break;

                await Task.Delay(options.ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        await client.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>把 UDP 客户端接进宿主生命周期。</summary>
internal sealed class UdpClientHostedService(
    ProtocolClientOptions options,
    IPacketCodec<object> codec,
    IClientHandler handler,
    Microsoft.Extensions.Logging.ILogger<UdpProtocolClient> logger)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = new UdpProtocolClient(options, codec, handler, logger);

        await client.ConnectAsync(stoppingToken).ConfigureAwait(false);

        // 保持存活直到停机
        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(500, stoppingToken).ConfigureAwait(false);

        await client.DisposeAsync().ConfigureAwait(false);
    }
}
