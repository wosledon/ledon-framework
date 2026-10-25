namespace Ledon.Sip.Server;

/// <summary>SIP 服务端选项。</summary>
public sealed class SipServerOptions
{
    /// <summary>UDP 监听地址，默认 <c>0.0.0.0</c>。</summary>
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>UDP 监听端口，默认 5060（SIP 标准端口）。</summary>
    public int Port { get; set; } = 5060;

    /// <summary>
    /// 是否同时监听 TCP，默认 <c>true</c>（RFC 3261 要求所有 UA 同时支持两者；
    /// TCP 也是大消息（带 SDP 的 INVITE 很容易超 MTU）的可靠通道）。
    /// </summary>
    public bool EnableTcp { get; set; } = true;

    /// <summary>TCP 监听端口，默认 5060（与 UDP 同端口，符合规范做法）。</summary>
    public int TcpPort { get; set; } = 5060;

    /// <summary>User-Agent / Server 头值，默认 <c>Ledon/1.0</c>。</summary>
    public string UserAgent { get; set; } = "Ledon/1.0";

    /// <summary>自动应答 OPTIONS（保活探测），默认 <c>true</c>。</summary>
    public bool AutoAnswerOptions { get; set; } = true;

    /// <summary>自动应答 REGISTER，默认 <c>false</c>（真实注册需鉴权与位置服务）。</summary>
    public bool AutoAnswerRegister { get; set; }
}
