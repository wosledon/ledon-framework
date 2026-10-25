namespace Ledon.Protocols.Sip;

/// <summary>SDP（RFC 4566）解析结果：会话描述的关键字段。</summary>
public sealed class Sdp
{
    /// <summary>会话名（<c>s=</c>）。</summary>
    public string? SessionName { get; init; }

    /// <summary>连接信息（<c>c=</c>，如 <c>IN IP4 10.0.0.1</c>）。</summary>
    public string? Connection { get; init; }

    /// <summary>媒体流列表（<c>m=</c>）。</summary>
    public IReadOnlyList<SdpMedia> Media { get; init; } = [];

    /// <summary>全部原始行（按顺序）。</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];
}

/// <summary>一个媒体流（<c>m=</c> 行 + 其后属性行）。</summary>
public sealed record SdpMedia
{
    /// <summary>媒体类型（audio/video/……）。</summary>
    public string Media { get; init; } = "";

    /// <summary>端口。</summary>
    public int Port { get; init; }

    /// <summary>传输协议（RTP/AVP、UDP/TLS/RTP/SAVPF……）。</summary>
    public string Protocol { get; init; } = "";

    /// <summary>载荷格式（编码/DTMF 事件号）。</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>附加属性行（<c>a=</c>，如 rtpmap/sendrecv）。</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];
}

/// <summary>SDP 解析器（只取关键字段，不修改原文）。</summary>
public static class SdpParser
{
    /// <summary>解析 SDP 文本；空输入返回空对象。</summary>
    public static Sdp Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Sdp();

        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 媒体流是"m= 行 + 其后属性行"的组合：按块收集后一次性构造（避免记录拷贝丢失）
        var blocks = new List<(string[] Header, List<string> Attributes)>();
        List<string>? attributes = null;
        string? sessionName = null;
        string? connection = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[1] != '=')
                continue;

            var type = line[0];
            var value = line[2..];

            switch (type)
            {
                case 's':
                    sessionName = value;
                    break;

                case 'c':
                    // 会话级连接信息（出现在第一个 m= 之前）
                    if (attributes is null)
                        connection = value;
                    break;

                case 'm':
                    var header = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    attributes = [];
                    blocks.Add((header, attributes));
                    break;

                case 'a':
                    attributes?.Add(value);
                    break;
            }
        }

        var media = blocks
            .Where(block => block.Header.Length >= 3)
            .Select(block => new SdpMedia
            {
                Media = block.Header[0],
                Port = int.TryParse(block.Header[1], out var port) ? port : 0,
                Protocol = block.Header[2],
                Formats = block.Header.Skip(3).ToArray(),
                Attributes = block.Attributes,
            })
            .ToArray();

        return new Sdp
        {
            SessionName = sessionName,
            Connection = connection,
            Media = media,
            Lines = lines,
        };
    }
}
