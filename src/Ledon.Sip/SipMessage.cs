using System.Text;

namespace Ledon.Sip;

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
/// <para>
/// 头字段按**首次出现顺序**保存，同名多值保序（Via/Route/Record-Route 的顺序
/// 在响应里必须原样带回，拼成一个字符串会丢信息）。行 continuation（obs-fold，
/// 规范 7.3.1）在解析时已展开。
/// </para>
/// </summary>
public sealed class SipMessage
{
    private readonly Dictionary<string, List<string>> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

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

    /// <summary>消息体（通常是 SDP 文本）。</summary>
    public string? Body { get; private set; }

    // ---------- 头字段 ----------

    /// <summary>取头的第一个值（大小写不敏感；不存在返回 <c>null</c>）。</summary>
    public string? Header(string name) =>
        _headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>取头的全部值（保序；不存在返回空数组）。</summary>
    public IReadOnlyList<string> HeaderValues(string name) =>
        _headers.TryGetValue(name, out var values) ? values : [];

    /// <summary>是否带头（大小写不敏感）。</summary>
    public bool HasHeader(string name) => _headers.ContainsKey(name);

    /// <summary>按出现顺序枚举全部头（每值一项）。</summary>
    public IEnumerable<(string Name, string Value)> EnumerateHeaders() =>
        _order.SelectMany(name => _headers[name].Select(value => (name, value)));

    /// <summary>替换一个头（不存在则新增）。</summary>
    public SipMessage WithHeader(string name, string value)
    {
        if (!_headers.TryGetValue(name, out var values))
        {
            values = [];
            _headers[name] = values;
            _order.Add(name);
        }

        values.Clear();
        values.Add(value);

        return this;
    }

    /// <summary>追加一个同名的头（Via 等多值头用）。</summary>
    public SipMessage AddHeader(string name, string value)
    {
        if (!_headers.TryGetValue(name, out var values))
        {
            values = [];
            _headers[name] = values;
            _order.Add(name);
        }

        values.Add(value);

        return this;
    }

    /// <summary>删掉一个头（大小写不敏感）。</summary>
    public SipMessage RemoveHeader(string name)
    {
        if (_headers.Remove(name))
            _order.Remove(name);

        return this;
    }

    // ---------- 便捷取头（字符串形态） ----------

    /// <summary>Call-ID（对话标识）。</summary>
    public string? CallId => Header("Call-ID");

    /// <summary>CSeq（序号 + 方法，如 <c>1 INVITE</c>）。</summary>
    public string? CSeq => Header("CSeq");

    /// <summary>From 头原文。</summary>
    public string? From => Header("From");

    /// <summary>To 头原文。</summary>
    public string? To => Header("To");

    /// <summary>最上层 Via 头原文（我们发出的下一跳）。</summary>
    public string? Via => Header("Via");

    /// <summary>Contact 头原文。</summary>
    public string? Contact => Header("Contact");

    /// <summary>Max-Forwards。</summary>
    public int? MaxForwards => int.TryParse(Header("Max-Forwards"), out var value) ? value : null;

    // ---------- 类型化视图 ----------

    /// <summary>From 解析成地址 + tag。</summary>
    public SipNameAddr? FromAddress => SipNameAddr.Parse(From);

    /// <summary>To 解析成地址 + tag。</summary>
    public SipNameAddr? ToAddress => SipNameAddr.Parse(To);

    /// <summary>Contact 解析成地址 + 参数。</summary>
    public SipNameAddr? ContactAddress => SipNameAddr.Parse(Contact);

    /// <summary>CSeq 解析成序号 + 方法。</summary>
    public SipCSeq? CSeqValue => SipCSeq.Parse(CSeq);

    /// <summary>全部 Via（保序，第一个是我们收到的入口）。</summary>
    public IReadOnlyList<SipVia> Vias => SipVia.ParseAll(this);

    /// <summary>最上层 Via（请求进来的那一跳）。</summary>
    public SipVia? TopVia => SipVia.Parse(Via);

    /// <summary>是否最终响应（2xx-6xx）。</summary>
    public bool IsFinalResponse => Type == SipMessageType.Response && StatusCode is >= 200 and <= 699;

    /// <summary>是否临时响应（1xx）。</summary>
    public bool IsProvisionalResponse => Type == SipMessageType.Response && StatusCode is >= 100 and <= 199;

    /// <summary>事务键：分支参数唯一标识一个事务（RFC 3261 §17.1.3）。</summary>
    public string? TransactionKey => TopVia?.Branch;

    // ---------- 序列化 ----------

    /// <summary>序列化为线上字节（CRLF 行尾，Content-Length 自动按 body 计算）。</summary>
    public byte[] ToBytes()
    {
        var builder = new StringBuilder();

        if (Type == SipMessageType.Request)
        {
            builder.Append(Method);
            builder.Append(' ');
            builder.Append(RequestUri);
            builder.Append(' ');
            builder.Append(Version);
        }
        else
        {
            builder.Append(Version);
            builder.Append(' ');
            builder.Append(StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(ReasonPhrase))
            {
                builder.Append(' ');
                builder.Append(ReasonPhrase);
            }
        }

        foreach (var (name, value) in EnumerateHeaders())
        {
            builder.Append("\r\n");
            builder.Append(name);
            builder.Append(": ");
            builder.Append(value);
        }

        // Content-Length 缺失时按 body 字节数补：少写它，对端会认为 body 没收完
        if (!HasHeader("Content-Length"))
        {
            builder.Append("\r\nContent-Length: ");
            builder.Append(Body is null ? 0 : Encoding.UTF8.GetByteCount(Body));
        }

        builder.Append("\r\n\r\n");

        if (!string.IsNullOrEmpty(Body))
            builder.Append(Body);

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>设置消息体（返回自身，便于链式构造）。</summary>
    public SipMessage WithBody(string? body)
    {
        Body = string.IsNullOrEmpty(body) ? null : body;

        return this;
    }
}
