# Ledon.Notifications.Push

推送通知渠道：接入 `Ledon.Notifications` 的统一路由（`INotificationChannel`），业务仍然只依赖 `INotificationSender`。

```csharp
builder.Services.AddLedonNotifications();
builder.Services.AddLedonPushNotifications(o =>
{
    o.Provider = PushProvider.Fcm;
    o.ProjectId = "my-project";
    o.AccessTokenProvider = ct => GetGoogleAccessTokenAsync(ct);
});
```

内置两种网关：

- `PushProvider.Webhook`：POST JSON `{ token, title, body, data }`，可选 Bearer Token；
- `PushProvider.Fcm`：Firebase Cloud Messaging HTTP v1（Bearer OAuth2 + `messages:send`）。

`Notification.Metadata` 会作为推送 data 透传；`TemplateName` 会附加到 data 的 `template` 字段。
扩展新网关实现 `IPushGateway` 并注册即可。

完整文档：<https://github.com/wosledon/ledon-framework>
