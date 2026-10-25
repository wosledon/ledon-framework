using Ledon.Streaming;
using Ledon.Streaming.Rtmp;
using Ledon.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 流媒体入口（控制面：鉴权、事件、转流、录制、WHIP/WHEP 信令）：
/// <code>
/// builder.Services.AddLedonStreaming(o =&gt;
/// {
///     o.Secret = "&lt;从配置中心注入&gt;";
///     o.ServerApiUrl = "http://127.0.0.1:1985";        // SRS OpenAPI
///     o.PublicRtmpUrl = "rtmp://media.example.com";
///     o.PublicHttpUrl = "http://media.example.com";
///     o.PublicWebRtcUrl = "webrtc://media.example.com/live";
/// });
/// </code>
/// 媒体面（转发/转码/录制）由媒体服务器（SRS/ZLMediaKit）承担，框架负责鉴权、
/// 事件、在线状态与控制 API——不重复造媒体内核。
/// </summary>
public static class LedonStreamingServiceCollectionExtensions
{
    /// <summary>注册流媒体能力（幂等）。</summary>
    public static IServiceCollection AddLedonStreaming(this IServiceCollection services)
        => services.AddLedonStreaming(_ => { });

    /// <summary>注册流媒体能力并配置。</summary>
    public static IServiceCollection AddLedonStreaming(
        this IServiceCollection services,
        Action<LedonStreamingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonStreamingOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.Secret))
            throw new InvalidOperationException("流媒体需要配置 Secret（推拉流令牌签名用）");

        services.TryAddSingleton(options);

        // 鉴权：默认令牌鉴权；业务可实现 IStreamAuthorizer 覆盖（TryAdd 不覆盖手动注册）
        services.TryAddSingleton<IStreamTokenService, HmacStreamTokenService>();
        services.TryAddSingleton<IStreamAuthorizer, TokenStreamAuthorizer>();

        // 在线状态与事件分发
        services.TryAddSingleton<IStreamRegistry, InMemoryStreamRegistry>();
        services.TryAddSingleton<StreamEventDispatcher>();
        services.TryAddSingleton<StreamUrlBuilder>();
        services.TryAddSingleton<IMediaServerClient, SrsMediaServerClient>();

        // 掉线扫描：媒体服务器不推"掉线"事件，靠心跳阈值兜底
        services.AddHostedService<StreamOfflineSweeper>();

        // 端点（框架包自身不进约定扫描，显式注册）
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, StreamWebhookEndpoint>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, WhipWhepEndpoints>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, StreamPlaybackEndpoints>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, HttpFlvEndpoints>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, HlsEndpoints>());

        // 媒体面：RTMP 推流/拉流服务器 + 流枢纽 + HLS 分片
        services.TryAddSingleton<Ledon.Streaming.Playback.StreamHub>();
        services.TryAddSingleton<Ledon.Streaming.Playback.StreamHubOptions>(
            _ => new Ledon.Streaming.Playback.StreamHubOptions());
        services.AddOptions();

        // 过期流清扫：停止发布超过保留时长的流从字典里删掉，否则内存只涨不跌
        services.AddHostedService<StreamHubSweeper>();
        // 选项经 IOptions 解析：调用方的 Configure<T> / 配置文件都能覆盖默认值
        services.TryAddSingleton<RtmpServerOptions>(provider =>
            provider.GetRequiredService<IOptions<RtmpServerOptions>>().Value);
        services.AddHostedService<RtmpServer>();
        services.TryAddSingleton<HlsOptions>(provider =>
            provider.GetRequiredService<IOptions<HlsOptions>>().Value);
        services.AddHostedService<HlsSegmenter>();

        return services;
    }
}

/// <summary>
/// 过期流清扫：定时把"停止发布超过保留时长"的流从 <see cref="Ledon.Streaming.Playback.StreamHub"/>
/// 里删掉。没有这一步，每条播过的流都会永远占着内存。
/// </summary>
internal sealed class StreamHubSweeper(
    Ledon.Streaming.Playback.StreamHub hub,
    Ledon.Streaming.Playback.StreamHubOptions options,
    ILogger<StreamHubSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SweepExpired)
            return;

        var interval = TimeSpan.FromTicks(Math.Max(options.UnpublishedRetention.Ticks / 2, TimeSpan.FromSeconds(1).Ticks));

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                var removed = hub.SweepExpired();

                if (removed > 0)
                    logger.LogInformation("清扫了 {Count} 条过期流（当前 {Total} 条）", removed, hub.Streams.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "过期流清扫失败（下次继续）");
            }
        }
    }
}

/// <summary>掉线扫描：定时把超过心跳阈值的流标记离线（补偿媒体服务器不推掉线事件）。</summary>
internal sealed class StreamOfflineSweeper(
    IStreamRegistry registry,
    LedonStreamingOptions options,
    ILogger<StreamOfflineSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.OfflineThreshold / 2);

        while (!stoppingToken.IsCancellationRequested)
        {
            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                if (registry is InMemoryStreamRegistry memory)
                    memory.SweepOffline();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "流离线扫描失败（下次继续）");
            }
        }
    }
}
