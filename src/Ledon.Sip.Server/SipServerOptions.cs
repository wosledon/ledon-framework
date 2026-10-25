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

    /// <summary>
    /// TCP 监听端口，默认 <c>null</c> = 跟随 <see cref="Port"/>（同端口，符合规范做法）。
    /// <para>
    /// 显式设成别的值时要小心：两个端口都要真的可用。设成 <c>null</c> 就不会出现
    /// "改了 UDP 端口、TCP 还钉在 5060"这种意外占用。
    /// </para>
    /// </summary>
    public int? TcpPort { get; set; }

    /// <summary>实际生效的 TCP 端口（<see cref="TcpPort"/> 为 null 时取 <see cref="Port"/>）。</summary>
    public int EffectiveTcpPort => TcpPort ?? Port;

    /// <summary>User-Agent / Server 头值，默认 <c>Ledon/1.0</c>。</summary>
    public string UserAgent { get; set; } = "Ledon/1.0";

    /// <summary>自动应答 OPTIONS（保活探测），默认 <c>true</c>。</summary>
    public bool AutoAnswerOptions { get; set; } = true;

    /// <summary>自动应答 REGISTER，默认 <c>false</c>（真实注册需鉴权与位置服务）。</summary>
    public bool AutoAnswerRegister { get; set; }
}
