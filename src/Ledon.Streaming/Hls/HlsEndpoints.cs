using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using System.Buffers;
using Ledon.Streaming;
using Ledon.Web;
using Ledon.Streaming.Media;
using Ledon.Streaming.Hls;
using Ledon.Streaming.Playback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>HLS 播放选项。</summary>
public sealed class HlsOptions
{
    /// <summary>单个分片时长（秒），默认 2（HLS 推荐 2-6 秒）。</summary>
    public int SegmentSeconds { get; set; } = 2;

    /// <summary>播放列表保留的分片数，默认 5（约 10 秒窗口）。</summary>
    public int PlaylistSize { get; set; } = 5;
}

/// <summary>
/// HLS 播放端点：`GET /live/{app}/{name}.m3u8` 返回直播播放列表，
/// `GET /live/{app}/{name}/{sequence}.ts` 返回分片。
/// <para>
/// 分片由发布中的流**实时切分**：按 <see cref="HlsOptions.SegmentSeconds"/>
/// 把 TS 包切成文件，写入内存缓存；播放列表始终指向最近 N 个分片。
/// </para>
/// </summary>
public sealed class HlsEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        builder.MapGet("/live/{app}/{name}.m3u8", (
            string app,
            string name,
            HttpContext context,
            CancellationToken ct) =>
        {
            var segmenter = context.RequestServices.GetRequiredService<HlsSegmenter>();
            var identity = new StreamIdentity(app, name);
            var segments = segmenter.GetSegments(identity);

            if (segments.Count == 0)
                return Results.NotFound($"流 {identity.Path} 不存在或未在发布");

            var builder2 = new StringBuilder();
            builder2.AppendLine("#EXTM3U");
            builder2.AppendLine("#EXT-X-VERSION:3");
            builder2.AppendLine("#EXT-X-TARGETDURATION:2");
            builder2.AppendLine("#EXT-X-MEDIA-SEQUENCE:" + Math.Max(0, segments.Count - segmenter.PlaylistSize));
            builder2.AppendLine("#EXT-X-PLAYLIST-TYPE:EVENT");

            foreach (var segment in segments.TakeLast(segmenter.PlaylistSize))
            {
                builder2.AppendLine(CultureInfo.InvariantCulture, $"#EXTINF:{segment.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture)},");
                builder2.AppendLine($"{segment.Sequence}.ts");
            }

            context.Response.ContentType = "application/vnd.apple.mpegurl";
            context.Response.Headers.CacheControl = "no-cache";

            return Results.Text(builder2.ToString(), "application/vnd.apple.mpegurl");
        });

        builder.MapGet("/live/{app}/{name}/{sequence}.ts", (
            string app,
            string name,
            int sequence,
            HttpContext context) =>
        {
            var segmenter = context.RequestServices.GetRequiredService<HlsSegmenter>();
            var identity = new StreamIdentity(app, name);
            var segment = segmenter.Find(identity, sequence);

            return segment is null
                ? Results.NotFound()
                : Results.File(segment.Data, "video/mp2t");
        });
    }
}

/// <summary>一个 HLS 分片。</summary>
public sealed record HlsSegment(int Sequence, byte[] Data, double DurationSeconds);

/// <summary>
/// HLS 分片器：订阅 <see cref="StreamHub"/> 的媒体流，按目标时长切成 TS 分片。
/// </summary>
public sealed class HlsSegmenter : BackgroundService
{
    private readonly ConcurrentDictionary<string, StreamSegments> _segments = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamHub _hub;
    private readonly HlsOptions _options;
    private readonly ILogger<HlsSegmenter> _logger;

    public HlsSegmenter(StreamHub hub, HlsOptions options, ILogger<HlsSegmenter> logger)
    {
        _hub = hub;
        _options = options;
        _logger = logger;
    }

    /// <summary>播放列表保留分片数。</summary>
    public int PlaylistSize => _options.PlaylistSize;

    /// <summary>取某条流的分片（无则空）。</summary>
    public IReadOnlyList<HlsSegment> GetSegments(StreamIdentity identity) =>
        _segments.TryGetValue(identity.Path, out var state)
            ? state.Segments.Select(segment => segment).ToArray()
            : [];

    /// <summary>按序号取分片。</summary>
    public HlsSegment? Find(StreamIdentity identity, int sequence) =>
        _segments.TryGetValue(identity.Path, out var state)
            ? state.Segments.FirstOrDefault(segment => segment.Sequence == sequence)
            : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 轮询发现新流并订阅：HLS 是"按需"协议，有观众请求时才切分
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);

            foreach (var stream in _hub.Streams)
            {
                if (!stream.IsPublished)
                    continue;

                var state = _segments.GetOrAdd(stream.Identity.Path, _ => new StreamSegments());

                if (!state.Started)
                {
                    state.Started = true;
                    state.Subscription = stream.Subscribe();
                    _ = Task.Run(() => SegmentLoopAsync(stream.Identity, state, stoppingToken), CancellationToken.None);
                }            }
        }
    }

    private async Task SegmentLoopAsync(StreamIdentity identity, StreamSegments state, CancellationToken cancellationToken)
    {
        if (state.Subscription is null)
            return;

        var muxer = new TsMuxer();
        var buffer = new ArrayBufferWriter<byte>();
        var segmentStart = 0L;
        var sequence = 0;

        try
        {
            await foreach (var tag in state.Subscription.Value.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                muxer.Write(buffer, tag);

                // 到达目标时长 → 切分
                if (tag.TimestampMs - segmentStart >= _options.SegmentSeconds * 1000)
                {
                    var data = buffer.WrittenSpan.ToArray();
                    buffer.Clear();

                    if (data.Length > 0)
                    {
                        var duration = (tag.TimestampMs - segmentStart) / 1000.0;
                        state.Add(new HlsSegment(sequence++, data, duration));
                        segmentStart = tag.TimestampMs;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停播
        }
        finally
        {
            state.Started = false;

            // 停播/取消：退订，否则观众断开后订阅者一直留在流上
            if (state.Subscription is not null)
                _hub.Unsubscribe(identity, state.Subscription.Value.Id);

            state.Subscription = null;
            _logger.LogDebug("流 {Stream} 的 HLS 分片结束", identity);
        }
    }

    private sealed class StreamSegments
    {
        private readonly Lock _gate = new();
        private readonly List<HlsSegment> _segments = [];

        public bool Started { get; set; }

        public StreamSubscription? Subscription { get; set; }

        public IReadOnlyList<HlsSegment> Segments
        {
            get
            {
                lock (_gate)
                    return _segments.ToArray();
            }
        }

        public void Add(HlsSegment segment)
        {
            lock (_gate)
            {
                _segments.Add(segment);

                // 只保留最近 20 个分片（播放列表取最后 N 个，多余的供重连观众）
                if (_segments.Count > 20)
                    _segments.RemoveRange(0, _segments.Count - 20);
            }
        }
    }
}
