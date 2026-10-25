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
