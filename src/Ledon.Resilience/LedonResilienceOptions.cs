namespace Ledon.Resilience;

/// <summary>弹性策略默认参数。默认值即为生产可用配置，按需微调。</summary>
public sealed class LedonResilienceOptions
{
    /// <summary>最大重试次数（针对 5xx/429/网络故障），默认 3。</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>重试基础延迟（指数退避 + 抖动），默认 2 秒。</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>单次尝试超时，默认 30 秒。</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>熔断打开前允许的连续失败比例阈值，默认 0.5。</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>熔断判定所需的最小采样数，默认 5。</summary>
    public int MinimumThroughput { get; set; } = 5;

    /// <summary>熔断持续时间，默认 30 秒。</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否对全部 IHttpClientFactory 客户端生效，默认开。</summary>
    public bool ApplyToAllHttpClients { get; set; } = true;
}
