namespace Ledon.Notifications;

/// <summary>通知选项。</summary>
public sealed class LedonNotificationOptions
{
    /// <summary>默认发件人（邮件）。</summary>
    public string? FromAddress { get; set; }

    /// <summary>发件人显示名。</summary>
    public string? FromDisplayName { get; set; }

    /// <summary>SMTP 主机（邮件渠道必填）。</summary>
    public string? SmtpHost { get; set; }

    /// <summary>SMTP 端口，默认 587（STARTTLS）。</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>SMTP 是否启用 SSL，默认 <c>false</c>（587 用 STARTTLS）。</summary>
    public bool SmtpUseSsl { get; set; }

    /// <summary>SMTP 用户名。</summary>
    public string? SmtpUserName { get; set; }

    /// <summary>SMTP 密码。</summary>
    public string SmtpPassword { get; set; } = "";

    /// <summary>
    /// 是否走持久化队列发送（默认 <c>true</c>）：渠道抖动自动重试、进程重启不丢。
    /// 关掉则同步直发（测试/开发用）。
    /// </summary>
    public bool UseDurableQueue { get; set; } = true;

    /// <summary>发送失败重试次数，默认 3。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// 重试退避起点，默认 1 秒。第 n 次重试等待 <c>base × 2^(n-1)</c>。
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>重试退避上限，默认 30 秒（防止重试次数多时等太久）。</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 退避是否加抖动（±25%），默认 <c>true</c>。
    /// 批量通知同时失败时，没有抖动会让所有重试在同一时刻再次冲击渠道。
    /// </summary>
    public bool RetryJitter { get; set; } = true;

    /// <summary>
    /// 批量发送的并发度，默认 8。通知是 IO 密集型，串行发送 100 条要约 100 倍延迟；
    /// 但并发过高可能被渠道限流，所以给个可调的上限而不是无界并行。
    /// 设为 1 则退化为串行。
    /// </summary>
    public int BatchConcurrency { get; set; } = 8;

    /// <summary>
    /// 消息去重键（可选）：相同键的通知只发送一次（默认 24 小时内）。
    /// 用于"至少一次"投递链路上游重复时的兜底——渠道实际送达但返回失败的场景。
    /// 置空则不去重。
    /// </summary>
    public Func<Notification, string?>? DeduplicationKey { get; set; }

    /// <summary>去重窗口，默认 24 小时。</summary>
    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>发送记录（运维排查：谁、发成功没有、渠道原话）。</summary>
public sealed record NotificationRecord(
    Guid Id,
    string Channel,
    string Recipient,
    string Subject,
    string? TemplateName,
    bool Success,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    int Attempts);

/// <summary>发送记录查询（内存实现；多实例/持久化可换数据库实现）。</summary>
public interface INotificationLog
{
    /// <summary>记录一次发送。</summary>
    void Record(Notification notification, NotificationResult result, int attempts);

    /// <summary>最近 N 条（倒序）。</summary>
    IReadOnlyList<NotificationRecord> Recent(int count = 50);

    /// <summary>按渠道统计成功率（运维面板用）。</summary>
    IReadOnlyDictionary<string, (int Total, int Failed)> Stats();
}
