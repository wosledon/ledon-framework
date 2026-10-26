# Ledon.Notifications.Sms

短信通知渠道：接入 `Ledon.Notifications` 的统一路由（`INotificationChannel`），业务仍然只依赖 `INotificationSender`。

```csharp
builder.Services.AddLedonNotifications();
builder.Services.AddLedonSmsNotifications(o =>
{
    o.Provider = SmsProvider.Webhook;
    o.Endpoint = "https://sms-gateway.example.com/send";
    o.AccessToken = "<Bearer Token>";
    o.From = "Ledon";
});
```

内置两种网关：

- `SmsProvider.Webhook`：POST JSON `{ to, from, body }`，可选 Bearer Token；
- `SmsProvider.Twilio`：Twilio Messages API（Basic Auth + form）。

扩展新网关实现 `ISmsGateway` 并注册即可。发送失败返回 `NotificationResult.Fail`，由 `INotificationSender` 的重试策略处理。

完整文档：<https://github.com/wosledon/ledon-framework>
