namespace Ledon.Sip.Client;

/// <summary>SIP 客户端选项。</summary>
public sealed class SipClientOptions
{
    /// <summary>目标地址，默认 <c>127.0.0.1</c>。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>目标端口，默认 5060。</summary>
    public int Port { get; set; } = 5060;

    /// <summary>User-Agent 头值，默认 <c>Ledon/1.0</c>。</summary>
    public string UserAgent { get; set; } = "Ledon/1.0";

    /// <summary>单次事务超时（Timer B），默认 32 秒（RFC 3261 规定值）。</summary>
    public TimeSpan TransactionTimeout { get; set; } = TimeSpan.FromSeconds(32);

    /// <summary>非 INVITE 重传间隔序列（Timer E/F 的 T1 起点，默认 500ms 起）。</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>最大重传次数（超限即视为失败），默认 7。</summary>
    public int MaxRetries { get; set; } = 7;
}
