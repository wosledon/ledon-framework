# Ledon.Notifications

通知发送：邮件（SMTP）/短信/推送统一入口，业务只发"通知"不关心渠道。

```csharp
builder.Services.AddLedonNotifications(o =>
{
    o.SmtpHost = "smtp.example.com";
    o.SmtpPort = 587;
    o.FromAddress = "noreply@example.com";
});

public class WelcomeMessage(INotificationSender sender)
{
    public Task SendAsync(string email, CancellationToken ct = default) =>
        sender.SendAsync(new Notification("email", email, "欢迎注册", "感谢注册 Ledon"), ct);
}
```

- 渠道可替换：实现 `INotificationSender` 即接入新渠道（短信网关、FCM/APNs），业务代码不动；
- 失败自动重试（默认 3 次，指数退避 1s/2s/4s），渠道错误不当异常抛给业务；
- 发送记录可查：`INotificationLog.Recent()` / `Stats()`（运维排查"邮件到底发没发"）；
- 未配置 SMTP 时不启动即失败——发送时返回失败结果并记录，不会静默丢消息；
- 需要可靠投递（进程重启不丢）时接持久化队列：`o.UseDurableQueue = true`（默认）。

完整文档：<https://github.com/wosledon/ledon-framework>
