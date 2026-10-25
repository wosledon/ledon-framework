using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Ledon.Streaming;

/// <summary>媒体服务器客户端：转流、录制、踢下线等控制面操作。</summary>
public interface IMediaServerClient
{
    /// <summary>列出当前全部流。</summary>
    Task<IReadOnlyList<MediaStreamInfo>> GetStreamsAsync(CancellationToken cancellationToken = default);

    /// <summary>踢下线（断开推流或某个观众）。</summary>
    Task<bool> KickoffAsync(StreamIdentity identity, string? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>开始录制（DVR）。</summary>
    Task<bool> StartDvrAsync(StreamIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>停止录制。</summary>
    Task<bool> StopDvrAsync(StreamIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>配置转流：把上游流拉到本地再推出去（转发/拉流分发）。</summary>
    Task<bool> ConfigureRelayAsync(StreamRelayRule rule, CancellationToken cancellationToken = default);

    /// <summary>应用转码Profile（多路码率/缩放）。</summary>
    Task<bool> ApplyTranscodeAsync(StreamIdentity identity, TranscodeProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>媒体服务器上报的流信息。</summary>
public sealed record MediaStreamInfo(
    StreamIdentity Identity,
    bool Online,
    int Players,
    string? VideoCodec,
    int? VideoWidth,
    int? VideoHeight,
    int? VideoBitrateKbps,
    int? AudioBitrateKbps);

/// <summary>转流规则：上游 → 本地（再对外提供）。</summary>
/// <param name="Name">规则名。</param>
/// <param name="UpstreamUrl">上游地址（rtmp/rtsp/srt/http-flv）。</param>
/// <param name="Target">本地目标流。</param>
/// <param name="Enabled">是否启用。</param>
public sealed record StreamRelayRule(string Name, string UpstreamUrl, StreamIdentity Target, bool Enabled = true);

/// <summary>转码 Profile。</summary>
/// <param name="Name">Profile 名。</param>
/// <param name="VideoCodec">视频编码（h264/h265）。</param>
/// <param name="Width">宽度（null = 保持）。</param>
/// <param name="Height">高度（null = 保持）。</param>
/// <param name="BitrateKbps">视频码率。</param>
/// <param name="AudioBitrateKbps">音频码率。</param>
public sealed record TranscodeProfile(
    string Name,
    string VideoCodec = "h264",
    int? Width = null,
    int? Height = null,
    int BitrateKbps = 1000,
    int AudioBitrateKbps = 128);

/// <summary>
/// SRS 客户端（OpenAPI v1）：<c>GET /api/v1/streams</c>、<c>/api/v1/clients</c>、
/// dvr 与转码经 SRS 的 OpenAPI 下发。未配置 API 地址时所有操作返回 <c>false</c>（不抛异常）。
/// </summary>
public sealed class SrsMediaServerClient : IMediaServerClient
{
    private readonly LedonStreamingOptions _options;
    private readonly HttpClient _http;

    public SrsMediaServerClient(LedonStreamingOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();

        if (!string.IsNullOrWhiteSpace(options.ServerApiUrl))
            _http.BaseAddress = new Uri(options.ServerApiUrl);
    }

    public async Task<IReadOnlyList<MediaStreamInfo>> GetStreamsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<SrsStreamsResponse>("/api/v1/streams", cancellationToken).ConfigureAwait(false);
        if (response?.Streams is null)
            return [];

        return response.Streams
            .Where(stream => !string.IsNullOrWhiteSpace(stream.Name))
            .Select(stream => new MediaStreamInfo(
                StreamIdentity.Parse(stream.Name!),
                stream.Publish?.Active == true,
                (int)(stream.Players ?? 0),
                stream.Video?.Codec?.Name,
                stream.Video?.Width is int width ? width : null,
                stream.Video?.Height is int height ? height : null,
                stream.Video?.Bitrate is int videoBitrate ? videoBitrate / 1000 : null,
                stream.Audio?.Bitrate is int audioBitrate ? audioBitrate / 1000 : null))
            .ToArray();
    }

    public async Task<bool> KickoffAsync(StreamIdentity identity, string? clientId = null, CancellationToken cancellationToken = default)
    {
        var query = clientId is null
            ? $"?stream={identity.Path}"
            : $"?client={clientId}";

        var response = await DeleteAsync($"/api/v1/clients{query}", cancellationToken).ConfigureAwait(false);

        return response;
    }

    public async Task<bool> StartDvrAsync(StreamIdentity identity, CancellationToken cancellationToken = default)
    {
        // SRS 的 DVR 经 HTTP 回调接口（/api/v1/dvrs）创建
        var payload = new { vhost = "__defaultVhost__", app = identity.App, stream = identity.Name };

        return await PostAsync("/api/v1/dvrs", payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> StopDvrAsync(StreamIdentity identity, CancellationToken cancellationToken = default)
    {
        var response = await DeleteAsync($"/api/v1/dvrs?stream={identity.Path}", cancellationToken).ConfigureAwait(false);

        return response;
    }

    public async Task<bool> ConfigureRelayAsync(StreamRelayRule rule, CancellationToken cancellationToken = default)
    {
        // SRS 的转发（forward）规则：把上游拉到本地 app/stream
        var payload = new
        {
            enabled = rule.Enabled,
            vhost = "__defaultVhost__",
            app = rule.Target.App,
            stream = rule.Target.Name,
            backend = rule.UpstreamUrl,
        };

        return await PostAsync("/api/v1/forwards", payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ApplyTranscodeAsync(StreamIdentity identity, TranscodeProfile profile, CancellationToken cancellationToken = default)
    {
        // SRS 的转码（transcode）Profile
        var payload = new
        {
            vhost = "__defaultVhost__",
            app = identity.App,
            stream = identity.Name,
            name = profile.Name,
            vcodec = profile.VideoCodec,
            vwidth = profile.Width,
            vheight = profile.Height,
            vbitrate = profile.BitrateKbps,
            abitrate = profile.AudioBitrateKbps,
        };

        return await PostAsync("/api/v1/transcodes", payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        if (NotConfigured())
            return null;

        try
        {
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<bool> PostAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        if (NotConfigured())
            return false;

        try
        {
            using var response = await _http.PostAsJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        if (NotConfigured())
            return false;

        try
        {
            using var response = await _http.DeleteAsync(path, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private bool NotConfigured() => string.IsNullOrWhiteSpace(_options.ServerApiUrl);

    private sealed class SrsStreamsResponse
    {
        [JsonPropertyName("streams")]
        public SrsStream[]? Streams { get; set; }
    }

    private sealed class SrsStream
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("publish")]
        public SrsPublish? Publish { get; set; }

        [JsonPropertyName("players")]
        public long? Players { get; set; }

        [JsonPropertyName("video")]
        public SrsVideo? Video { get; set; }

        [JsonPropertyName("audio")]
        public SrsAudio? Audio { get; set; }
    }

    private sealed class SrsPublish
    {
        [JsonPropertyName("active")]
        public bool Active { get; set; }
    }

    private sealed class SrsVideo
    {
        [JsonPropertyName("codec")]
        public SrsCodec? Codec { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("bitrate")]
        public int? Bitrate { get; set; }
    }

    private sealed class SrsAudio
    {
        [JsonPropertyName("bitrate")]
        public int? Bitrate { get; set; }
    }

    private sealed class SrsCodec
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
