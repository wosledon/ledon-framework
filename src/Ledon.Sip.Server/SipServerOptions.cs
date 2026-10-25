namespace Ledon.Sip.Server;

/// <summary>SIP 服务端选项。</summary>
public sealed class SipServerOptions
{
    /// <summary>监听地址，默认 <c>0.0.0.0</c>。</summary>
    public string Address { get; set; } = "0.0.0.0";

    /// <summary>监听端口，默认 5060（SIP 标准端口）。</summary>
    public int Port { get; set; } = 5060;

    /// <summary>User-Agent / Server 头值，默认 <c>Ledon/1.0</c>。</summary>
    public string UserAgent { get; set; } = "Ledon/1.0";

    /// <summary>自动应答 OPTIONS（保活探测），默认 <c>true</c>。</summary>
    public bool AutoAnswerOptions { get; set; } = true;
}
