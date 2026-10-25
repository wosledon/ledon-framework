namespace Ledon.Sip;

/// <summary>
/// SIP 地址（From/To/Contact 的解析结果）：
/// <c>"显示名" &lt;sip:user@host&gt;;param=value</c>。
/// </summary>
public sealed class SipNameAddr
{
    /// <summary>显示名（可能为 null，如 <c>From: &lt;sip:a@b&gt;;tag=1</c>）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>URI 原文（如 <c>sip:user@host</c>，不含尖括号）。</summary>
    public string? Uri { get; init; }

    /// <summary>参数（tag/expires/q/……），大小写不敏感。</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>取参数（不存在返回 null）。</summary>
    public string? Parameter(string name) =>
        Parameters.TryGetValue(name, out var value) ? value : null;

    /// <summary>tag 参数（对话标识，UAC 必须生成、UAS 必须补全）。</summary>
    public string? Tag => Parameter("tag");

    /// <summary>URI 里的 user 部分（<c>sip:user@host</c> → <c>user</c>）。</summary>
    public string? User
    {
        get
        {
            var uri = Uri;
            if (uri is null)
                return null;

            var colon = uri.IndexOf(':');
            var at = uri.IndexOf('@');
            if (at < 0 || colon < 0 || at < colon)
                return null;

            return uri[(colon + 1)..at];
        }
    }

    /// <summary>URI 里的 host 部分。</summary>
    public string? Host
    {
        get
        {
            var uri = Uri;
            if (uri is null)
                return null;

            var at = uri.IndexOf('@');
            var host = at >= 0 ? uri[(at + 1)..] : uri;

            var colon = host.IndexOf(':');
            if (colon >= 0 && !host.StartsWith('['))
                host = host[..colon];

            return host;
        }
    }

    /// <summary>解析；失败返回 null（解析失败不该让整条消息不可用）。</summary>
    public static SipNameAddr? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        string? displayName = null;

        // "显示名" <uri>;params
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end < 0)
                return null;

            displayName = value[1..end];
            value = value[(end + 1)..].TrimStart();
        }

        string? uri;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (value.StartsWith('<'))
        {
            var end = value.IndexOf('>');
            if (end < 0)
                return null;

            uri = value[1..end];
            value = value[(end + 1)..].TrimStart();
        }
        else
        {
            // 裸 URI：到第一个 ; 为止
            var end = value.IndexOf(';');
            uri = end < 0 ? value : value[..end];
            value = end < 0 ? "" : value[end..];
        }

        foreach (var parameter in SplitParameters(value))
        {
            var eq = parameter.IndexOf('=');
            if (eq <= 0)
                continue;

            var name = parameter[..eq].Trim();
            var parameterValue = parameter[(eq + 1)..].Trim().Trim('"');

            parameters[name] = parameterValue;
        }

        return new SipNameAddr
        {
            DisplayName = displayName,
            Uri = uri,
            Parameters = parameters,
        };
    }

    /// <summary>按分号切参数（尖括号内的分号不算）。</summary>
    internal static IEnumerable<string> SplitParameters(string text)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ';' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (start < text.Length)
            yield return text[start..];
    }
}

/// <summary>Via 头的解析结果：<c>SIP/2.0/UDP host:port;branch=...;received=...</c>。</summary>
public sealed class SipVia
{
    /// <summary>协议+版本（<c>SIP/2.0</c>）。</summary>
    public string Protocol { get; init; } = "SIP/2.0";

    /// <summary>传输协议（UDP/TCP/TLS/……）。</summary>
    public string Transport { get; init; } = "UDP";

    /// <summary>主机（IP 或域名）。</summary>
    public string? Host { get; init; }

    /// <summary>端口（未指定为 0）。</summary>
    public int Port { get; init; }

    /// <summary>参数（branch/received/rport/ttl/maddr），大小写不敏感。</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>取参数（不存在返回 null）。</summary>
    public string? Parameter(string name) =>
        Parameters.TryGetValue(name, out var value) ? value : null;

    /// <summary>branch 参数（事务键，客户端必须以 <c>z9hG4bK-</c> 开头）。</summary>
    public string? Branch => Parameter("branch");

    /// <summary>received 参数（NAT 场景对端观察到的地址）。</summary>
    public string? Received => Parameter("received");

    /// <summary>rport 参数（空值表示"请回填我观察到的端口"）。</summary>
    public string? RPort => Parameter("rport");

    /// <summary>是否 TCP/TLS 传输。</summary>
    public bool IsReliable => Transport is "TCP" or "TLS" or "SCTP";

    /// <summary>解析单个 Via 头；失败返回 null。</summary>
    public static SipVia? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Trim().Split(';');
        var sent = parts[0].Trim().Split(' ');

        var protocol = "SIP/2.0";
        var transport = "UDP";
        var hostPart = sent[^1];

        if (sent.Length >= 3)
        {
            protocol = sent[0];
            transport = sent[1].ToUpperInvariant();
            hostPart = sent[2];
        }
        else if (sent.Length == 2)
        {
            transport = sent[0].ToUpperInvariant();
            hostPart = sent[1];
        }

        string? host = null;
        var port = 0;

        var colon = hostPart.LastIndexOf(':');
        if (colon >= 0 && !hostPart.StartsWith('['))
        {
            host = hostPart[..colon];
            _ = int.TryParse(hostPart[(colon + 1)..], out port);
        }
        else
        {
            host = hostPart;
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parts.Skip(1))
        {
            var eq = parameter.IndexOf('=');
            if (eq < 0)
            {
                parameters[parameter.Trim()] = "";
                continue;
            }

            parameters[parameter[..eq].Trim()] = parameter[(eq + 1)..].Trim();
        }

        return new SipVia
        {
            Protocol = protocol,
            Transport = transport,
            Host = host,
            Port = port,
            Parameters = parameters,
        };
    }

    /// <summary>解析一条消息里的全部 Via（保序）。</summary>
    public static IReadOnlyList<SipVia> ParseAll(SipMessage message) =>
        message.HeaderValues("Via").Select(Parse).Where(via => via is not null).ToArray()!;
}

/// <summary>CSeq 头的解析结果：<c>序号 方法</c>。</summary>
public sealed class SipCSeq
{
    /// <summary>序号（32 位无符号，每个对话内递增）。</summary>
    public uint Number { get; init; }

    /// <summary>方法（INVITE/ACK/BYE/……）。</summary>
    public string Method { get; init; } = "";

    /// <summary>序列化回 <c>序号 方法</c>。</summary>
    public override string ToString() =>
        $"{Number.ToString(System.Globalization.CultureInfo.InvariantCulture)} {Method}";

    /// <summary>解析；失败返回 null。</summary>
    public static SipCSeq? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !uint.TryParse(parts[0], out var number))
            return null;

        return new SipCSeq { Number = number, Method = parts[1].Trim().ToUpperInvariant() };
    }
}
