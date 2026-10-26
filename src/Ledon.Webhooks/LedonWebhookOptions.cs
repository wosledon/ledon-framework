namespace Ledon.Webhooks;

/// <summary>出站 Webhook 选项。</summary>
public sealed class LedonWebhookOptions
{
    /// <summary>单次投递最大尝试次数（含首次），默认 3。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>重试基础延迟（指数退避），默认 500ms。</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>单次 HTTP 请求超时，默认 10 秒。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>待投递队列容量，默认 1000。</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>已投递/死信记录保留条数，默认 1000。</summary>
    public int DeliveryHistorySize { get; set; } = 1000;

    /// <summary>管理端点是否要求授权，默认 <c>false</c>（生产建议开启并注册认证方案）。</summary>
    public bool RequireAuthorization { get; set; }

    /// <summary>自定义 HTTP 消息处理器（测试注入）；生产无需设置。</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}
