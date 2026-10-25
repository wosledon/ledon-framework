using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Streaming;
using Ledon.Streaming.Rtmp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.StreamSample;

/// <summary>
/// 流媒体模块：把 RTMP 媒体面 + 控制面注册进容器，并在初始化完成后打印可用地址。
/// <para>
/// 模块不是必须的（<c>AddLedonStreaming</c> 可以直接在 Program.cs 里调），
/// 但把能力收进模块后，"加一个功能 = 加一个程序集引用"，宿主一行不改——
/// 这是框架的解耦点。
/// </para>
/// </summary>
public sealed class StreamSampleModule : IModule
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddLedonStreaming(o =>
        {
            o.Secret = "stream-sample-secret";
            o.PublicHttpUrl = $"http://127.0.0.1:{HttpPort}";
            o.PublicRtmpUrl = $"rtmp://127.0.0.1:{RtmpPort}";
        });

        services.Configure<RtmpServerOptions>(options =>
        {
            options.Address = "127.0.0.1";
            options.Port = RtmpPort;
            options.OutgoingChunkSize = 4096;

            options.PingInterval = TimeSpan.FromSeconds(30);   // 保活：拔网线的对端只有心跳能发现
            options.IdleTimeout = TimeSpan.FromMinutes(2);     // 只连不发的连接不永久占资源
            options.MaxConnections = 100;

            // 示例放开推流鉴权；生产设 true 并注册自己的 IRtmpAuthenticator
            options.RequirePublishToken = false;
            options.RequirePlayToken = false;
        });
    }

    /// <inheritdoc />
    public Task OnInitializedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILogger<StreamSampleModule>>();

        logger.LogInformation(
            "流媒体就绪：RTMP rtmp://127.0.0.1:{Rtmp}/live/{{name}}，HTTP-FLV http://127.0.0.1:{Http}/live/live/{{name}}.flv",
            RtmpPort, HttpPort);

        return Task.CompletedTask;
    }

    /// <summary>模块自己的端口（示例写死成动态分配，由 <see cref="Program"/> 回填）。</summary>
    public static int RtmpPort { get; set; }

    /// <summary>HTTP 端口（HTTP-FLV / HLS 播放用）。</summary>
    public static int HttpPort { get; set; }
}
