# Ledon.Notifications.Feishu

飞书接入：**发消息**（自定义机器人 / 自建应用）+ **收消息**（事件回调），一个包装齐。

## 发消息

```csharp
builder.Services.AddLedonFeishu(o =>
{
    o.AppId = "cli_xxx";                 // 自建应用：给单个人/会话发
    o.AppSecret = "xxx";
    o.WebhookUrl = "https://open.feishu.cn/open-apis/bot/v2/hook/xxx";   // 自定义机器人：发群
    o.WebhookSecret = "xxx";             // 机器人签名密钥（开启签名校验时）
});

// 群通知（最简）
await client.SendTextAsync("部署完成：v1.2.3");

// 卡片
await client.SendCardAsync(new { elements = new[] { new { tag = "div", text = new { content = "hello" } } } });

// 单聊（需 im:message:send 权限）
await client.SendToUserAsync(openId, "你的订单已发货", receiveIdType: "open_id");

// 接入统一通知路由（渠道名 feishu，与 email 同一套写法）
builder.Services.AddLedonFeishuNotifications();
await sender.SendAsync(new Notification("feishu", openId, "告警", "CPU 90%"), ct);
```

## 收消息（事件回调）

```csharp
builder.Services.AddLedonFeishu(o =>
{
    o.EncryptKey = "xxx";            // 配了就自动解密
    o.VerificationToken = "xxx";     // 配了就校验来源
    o.EventRoute = "/feishu/events"; // 飞书后台填这个地址
});

public class MessageHandler : IFeishuEventHandler
{
    public Task HandleAsync(FeishuEvent @event, CancellationToken ct = default)
    {
        if (@event.EventType == "im.message.receive_v1")
        {
            var message = @event.Event.Deserialize<FeishuMessageEvent>();
            // 回复、命令解析……
        }
        return Task.CompletedTask;
    }
}
```

- URL 校验（`url_verification`）自动应答 `challenge`；
- 回调内容 AES-256-CBC 解密（密钥 = SHA256(EncryptKey)）；
- Token 校验失败返回 401；处理器异常只记录（飞书按 200 判定投递成功，重推不会丢）；
- Lark 国际版把 `BaseUrl` 改成 `https://open.larksuite.com`。

完整文档：<https://github.com/wosledon/ledon-framework>
