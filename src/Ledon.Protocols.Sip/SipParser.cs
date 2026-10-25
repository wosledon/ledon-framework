using System.Text;

namespace Ledon.Protocols.Sip;

/// <summary>SIP 解析异常（协议不同步/消息不合法）。</summary>
public sealed class SipParseException(string message) : Exception(message);

/// <summary>
/// SIP 消息解析器（RFC 3261 子集）：请求行/状态行 + 头字段 + body。
/// 头字段名大小写不敏感，多行同名头按出现顺序拼接（Via 等）。
/// </summary>
public static class SipParser
{
    /// <summary>解析一条 SIP 消息（UTF-8 文本）。</summary>
    public static SipMessage Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SipParseException("SIP 消息为空");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        // 1) 起始行：请求行 "METHOD URI VERSION" 或 状态行 "VERSION CODE PHRASE"
        var startLine = lines[index++].Trim();
        if (startLine.Length == 0)
            throw new SipParseException("SIP 消息为空");

        SipMessageType type;
        string? method = null;
        string? requestUri = null;
        string version = "SIP/2.0";
        var statusCode = 0;
        string? reasonPhrase = null;

        if (startLine.StartsWith("SIP/", StringComparison.OrdinalIgnoreCase))
        {
            // 状态行
            var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out statusCode))
                throw new SipParseException($"非法的 SIP 状态行：{startLine}");

            type = SipMessageType.Response;
            version = parts[0];
            reasonPhrase = parts.Length > 2 ? parts[2] : "";
        }
        else
        {
            // 请求行
            var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                throw new SipParseException($"非法的 SIP 请求行：{startLine}");

            type = SipMessageType.Request;
            method = parts[0].ToUpperInvariant();
            requestUri = parts[1];
            version = parts[2];
        }

        // 2) 头字段：到空行结束
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        for (; index < lines.Length; index++)
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

            if (headers.TryGetValue(name, out var existing))
                headers[name] = $"{existing}, {value}";   // 同名多值（Via/Route/Record-Route）
            else
            {
                headers[name] = value;
                order.Add(name);
            }
        }

        // 3) body（通常是 SDP）：空行之后的全部内容
        string? body = index < lines.Length ? string.Join("\n", lines[index..]).Trim() : null;

        return new SipMessage
        {
            Type = type,
            Method = method,
            RequestUri = requestUri,
            Version = version,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers = headers,
            Body = string.IsNullOrEmpty(body) ? null : body,
        };
    }

    /// <summary>解析字节（UTF-8）。</summary>
    public static SipMessage Parse(ReadOnlySpan<byte> payload) =>
        Parse(Encoding.UTF8.GetString(payload));

    /// <summary>构造请求（便捷方法）。</summary>
    public static SipMessage CreateRequest(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body = null) =>
        new()
        {
            Type = SipMessageType.Request,
            Method = method.ToUpperInvariant(),
            RequestUri = requestUri,
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            Body = body,
        };

    /// <summary>构造响应（便捷方法）。</summary>
    public static SipMessage CreateResponse(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body = null) =>
        new()
        {
            Type = SipMessageType.Response,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            Body = body,
        };
}
