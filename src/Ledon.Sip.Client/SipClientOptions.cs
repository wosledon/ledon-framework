namespace Ledon.Sip.Client;

/// <summary>SIP 客户端选项。</summary>
public sealed class SipClientOptions
{
    /// <summary>目标地址，默认 <c>127.0.0.1</c>。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>目标端口，默认 5060。</summary>
    public int Port { get; set; } = 5060;

    /// <summary>
    /// 传输协议，默认 <see cref="SipTransport.Udp"/>。
    /// TCP 上按 Content-Length 切帧，且没有重传（TCP 自己保证可靠）。
    /// </summary>
    public SipTransport Transport { get; set; } = SipTransport.Udp;

    /// <summary>本地绑定端口，默认 0（系统分配）。Via/Contact 会带上这个端口。</summary>
    public int LocalPort { get; set; }

    /// <summary>本地绑定地址，默认 <c>null</c>（任意）。</summary>
    public string? LocalBindAddress { get; set; }

    /// <summary>User-Agent 头值，默认 <c>Ledon/1.0</c>。</summary>
    public string UserAgent { get; set; } = "Ledon/1.0";

    /// <summary>Contact 里的本地地址（默认取 <see cref="LocalBindAddress"/> 或 127.0.0.1）。</summary>
    public string? ContactHost { get; set; }

    /// <summary>单次事务超时（Timer B），默认 32 秒（RFC 3261 规定值）。</summary>
    public TimeSpan TransactionTimeout { get; set; } = TimeSpan.FromSeconds(32);

    /// <summary>重传间隔起点（Timer E/F 的 T1），默认 500 毫秒。</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>最大重传次数（超限即视为失败），默认 7。</summary>
    public int MaxRetries { get; set; } = 7;

    /// <summary>连接超时，默认 10 秒。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>SIP 传输协议。</summary>
public enum SipTransport
{
    /// <summary>UDP（有重传、无连接）。</summary>
    Udp = 0,

    /// <summary>TCP（可靠、按 Content-Length 切帧、无重传）。</summary>
    Tcp = 1,
}
