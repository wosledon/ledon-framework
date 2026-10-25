using System.Text;

namespace Ledon.Sip;

/// <summary>SIP 解析异常（协议不同步/消息不合法）。</summary>
public sealed class SipParseException(string message) : Exception(message);

/// <summary>
/// SIP 消息解析器（RFC 3261 子集）：请求行/状态行 + 头字段 + body。
/// <list type="bullet">
///   <item>头字段名大小写不敏感；</item>
///   <item>同名多值**保序**（Via/Route/Record-Route 的顺序在响应里必须原样带回）；</item>
///   <item>行 continuation（obs-fold，规范 7.3.1）解析时展开；</item>
///   <item>大小上限保护：超过 <see cref="MaxMessageSize"/> 直接抛异常，不被半条消息撑爆内存；</item>
///   <item>请求/响应的必备头（Via/From/To/Call-ID/CSeq）缺失时抛异常——缺头的消息
///         无法回包，早失败比晚失败好排查。</item>
/// </list>
/// </summary>
public static class SipParser
{
    /// <summary>单条消息上限，默认 64KB（SIP over UDP 通常远小于此）。</summary>
    public static int MaxMessageSize { get; set; } = 64 * 1024;

    /// <summary>解析一条 SIP 消息（UTF-8 文本）。</summary>
    public static SipMessage Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SipParseException("SIP 消息为空");

        if (text.Length > MaxMessageSize)
            throw new SipParseException($"SIP 消息超过上限 {MaxMessageSize} 字节（{text.Length}）");

        var lines = Unfold(text);
        var index = 0;

        // 1) 起始行：请求行 "METHOD URI VERSION" 或 状态行 "VERSION CODE PHRASE"
        var startLine = lines[index++].Trim();
        if (startLine.Length == 0)
            throw new SipParseException("SIP 起始行为空");

        SipMessageType type;
        string? method = null;
        string? requestUri = null;
        var version = "SIP/2.0";
        var statusCode = 0;
        string? reasonPhrase = null;

        if (startLine.StartsWith("SIP/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out statusCode))
                throw new SipParseException($"非法的 SIP 状态行：{startLine}");

            type = SipMessageType.Response;
            version = parts[0];
            reasonPhrase = parts.Length > 2 ? parts[2] : "";
        }
        else
        {
            var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                throw new SipParseException($"非法的 SIP 请求行：{startLine}");

            type = SipMessageType.Request;
            method = parts[0].ToUpperInvariant();
            requestUri = parts[1];
            version = parts[2];
        }

        // 2) 头字段：到空行结束（obs-fold 已在 Unfold 里处理）
        var message = new SipMessage
        {
            Type = type,
            Method = method,
            RequestUri = requestUri,
            Version = version,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
        };

        for (; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                index++;   // 跳过空行，指向 body
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new SipParseException($"非法的 SIP 头行：{line}");

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Length == 0)
                throw new SipParseException($"SIP 头名为空：{line}");

            message.AddHeader(name, value);
        }

        // 3) body（通常是 SDP）：空行之后的全部内容。
        //    原样保留、不裁剪：Content-Length 按 body 字节数算，裁剪会让重发出去的
        //    消息比声明长度短，TCP 对端会一直等 body 收完
        var body = index < lines.Count ? string.Join("\r\n", lines[index..]) : null;

        message.WithBody(body);

        Validate(message);

        return message;
    }

    /// <summary>解析字节（UTF-8）。</summary>
    public static SipMessage Parse(ReadOnlySpan<byte> payload) =>
        Parse(Encoding.UTF8.GetString(payload));

    /// <summary>必备头校验：缺一个就没法回包，早失败早排查。</summary>
    private static void Validate(SipMessage message)
    {
        if (message.Type == SipMessageType.Request && string.IsNullOrEmpty(message.Method))
            throw new SipParseException("请求缺少方法");

        foreach (var required in new[] { "Via", "From", "To", "Call-ID", "CSeq" })
        {
            if (!message.HasHeader(required))
                throw new SipParseException($"SIP 消息缺少必备头 {required}");
        }

        if (message.Type == SipMessageType.Request && SipCSeq.Parse(message.CSeq) is null)
            throw new SipParseException($"非法的 CSeq：{message.CSeq}");
    }

    /// <summary>
    /// 拆行并展开行 continuation（obs-fold，规范 7.3.1）：
    /// 以 SP/HTAB 开头的行是上一条头的延续，拼回上一条（丢掉的换行换成单空格）。
    /// </summary>
    private static List<string> Unfold(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var raw = normalized.Split('\n');
        var lines = new List<string>(raw.Length);

        foreach (var line in raw)
        {
            if (lines.Count > 0 && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                // continuation：拼到上一条（多个连续空白合成一个空格）
                var previous = lines[^1];
                var continuation = line.Trim();
                lines[^1] = previous.Length == 0 ? continuation : $"{previous} {continuation}";

                continue;
            }

            lines.Add(line);
        }

        // 只去首部空行：尾部空行可能是 body 的一部分（body 以换行结尾时），
        // 裁掉就会让重发消息的长度和 Content-Length 对不上
        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);

        return lines;
    }

    /// <summary>构造请求（便捷方法）。</summary>
    public static SipMessage CreateRequest(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body = null)
    {
        var message = new SipMessage
        {
            Type = SipMessageType.Request,
            Method = method.ToUpperInvariant(),
            RequestUri = requestUri,
        };

        foreach (var (name, value) in headers)
            message.AddHeader(name, value);

        return message.WithBody(body);
    }

    /// <summary>构造响应（便捷方法）。</summary>
    public static SipMessage CreateResponse(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body = null)
    {
        var message = new SipMessage
        {
            Type = SipMessageType.Response,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
        };

        foreach (var (name, value) in headers)
            message.AddHeader(name, value);

        return message.WithBody(body);
    }
}
