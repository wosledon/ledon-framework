using System.Text;

namespace Ledon.Protocols.Sip;

/// <summary>SIP 消息类型。</summary>
public enum SipMessageType
{
    /// <summary>请求（INVITE/ACK/BYE/REGISTER/OPTIONS/CANCEL/……）。</summary>
    Request = 0,

    /// <summary>响应（带状态码）。</summary>
    Response = 1,
}

/// <summary>
/// SIP 消息（RFC 3261）：请求行/状态行 + 头字段 + 可选 body（通常是 SDP）。
/// 只做解析与序列化，不含事务状态机——协议解析与策略分离。
/// </summary>
public sealed class SipMessage
{
    /// <summary>消息类型。</summary>
    public SipMessageType Type { get; init; }

    /// <summary>请求方法（请求消息用，如 INVITE）。</summary>
    public string? Method { get; init; }

    /// <summary>请求 URI（请求消息用）。</summary>
    public string? RequestUri { get; init; }

    /// <summary>SIP 版本，默认 <c>SIP/2.0</c>。</summary>
    public string Version { get; init; } = "SIP/2.0";

    /// <summary>状态码（响应消息用，如 200）。</summary>
    public int StatusCode { get; init; }

    /// <summary>原因短语（响应消息用，如 OK）。</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>头字段（保留原始大小写与顺序；同名多值用逗号拼接）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>消息体（通常是 SDP 文本）。</summary>
    public string? Body { get; init; }

    /// <summary>便捷取头（大小写不敏感；不存在返回 <c>null</c>）。</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Call-ID（对话标识）。</summary>
    public string? CallId => Header("Call-ID") ?? Header("Call-Id");

    /// <summary>CSeq（序号 + 方法，如 <c>1 INVITE</c>）。</summary>
    public string? CSeq => Header("CSeq");

    /// <summary>From 头（含 tag）。</summary>
    public string? From => Header("From");

    /// <summary>To 头（含 tag）。</summary>
    public string? To => Header("To");

    /// <summary>Via 头（路由用，可能多行）。</summary>
    public string? Via => Header("Via");

    /// <summary>Contact 头。</summary>
    public string? Contact => Header("Contact");

    /// <summary>序列化为线上字节（CRLF 行尾，符合 RFC 3261）。</summary>
    public byte[] ToBytes()
    {
        var builder = new StringBuilder();

        if (Type == SipMessageType.Request)
            builder.AppendLine($"{Method} {RequestUri} {Version}");
        else
            builder.AppendLine($"{Version} {StatusCode} {ReasonPhrase}".TrimEnd());

        foreach (var (name, value) in Headers)
            builder.AppendLine($"{name}: {value}");

        builder.AppendLine();

        if (!string.IsNullOrEmpty(Body))
            builder.Append(Body);

        // RFC 3261 要求 CRLF
        var text = builder.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");

        return Encoding.UTF8.GetBytes(text);
    }
}
