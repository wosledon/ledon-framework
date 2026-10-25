namespace Ledon.Streaming;

/// <summary>流协议。</summary>
public enum StreamProtocol
{
    /// <summary>RTMP（推流/拉流，兼容性最好）。</summary>
    Rtmp = 0,

    /// <summary>HTTP-FLV（拉流，低延迟，浏览器需 flv.js）。</summary>
    HttpFlv = 1,

    /// <summary>HLS（拉流，兼容性最好，延迟数秒）。</summary>
    Hls = 2,

    /// <summary>WebRTC（拉流/推流，亚秒级延迟，浏览器原生）。</summary>
    WebRtc = 3,

    /// <summary>RTSP（监控/摄像头推流）。</summary>
    Rtsp = 4,

    /// <summary>SRT（低延迟可靠传输）。</summary>
    Srt = 5,
}

/// <summary>流动作。</summary>
public enum StreamAction
{
    /// <summary>推流（发布）。</summary>
    Publish = 0,

    /// <summary>拉流（播放）。</summary>
    Play = 1,
}

/// <summary>流标识：应用名 + 流名 + 可选参数。</summary>
/// <param name="App">应用名（如 live）。</param>
/// <param name="Name">流名（如 camera-01）。</param>
/// <param name="Params">额外参数（如清晰度、token 之外的查询参数）。</param>
public sealed record StreamIdentity(string App, string Name, IReadOnlyDictionary<string, string>? Params = null)
{
    /// <summary>规范路径 <c>{App}/{Name}</c>（鉴权签名用的主体）。</summary>
    public string Path => $"{App}/{Name}";

    /// <summary>解析 <c>{App}/{Name}</c> 形式的路径。</summary>
    public static StreamIdentity Parse(string path, IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var parts = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"流路径应为 {{App}}/{{Name}} 形式：{path}", nameof(path));

        return new StreamIdentity(parts[0], parts[1], parameters);
    }

    /// <inheritdoc />
    public override string ToString() => Path;
}

/// <summary>流的实时状态（注册表项）。</summary>
public sealed class StreamStatus
{
    /// <summary>流标识。</summary>
    public required StreamIdentity Identity { get; init; }

    /// <summary>是否在线（已发布）。</summary>
    public bool Online { get; set; }

    /// <summary>发布者客户端 ID。</summary>
    public string? PublisherId { get; set; }

    /// <summary>发布者 IP。</summary>
    public string? PublisherIp { get; set; }

    /// <summary>使用的推流协议。</summary>
    public StreamProtocol? PublishProtocol { get; set; }

    /// <summary>当前播放人数。</summary>
    public int PlayerCount { get; set; }

    /// <summary>开始时间（UTC）。</summary>
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最近心跳（UTC）。</summary>
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>附加元数据（编码信息、设备位置等）。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
