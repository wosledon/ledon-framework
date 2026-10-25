# 飞书 / Socket / SIP

三方接入能力：飞书（通知 + 事件回调）、TCP/UDP 服务端与协议解析、SIP 协议栈。

## Ledon.Notifications.Feishu —— 飞书

```csharp
builder.Services.AddLedonFeishu(o =>
{
    o.AppId = "cli_xxx";                 // 自建应用：给单个人/会话发
    o.AppSecret = "xxx";
    o.WebhookUrl = "https://open.feishu.cn/open-apis/bot/v2/hook/xxx";   // 自定义机器人：发群
    o.WebhookSecret = "xxx";             // 机器人签名密钥
    o.EncryptKey = "xxx";                // 事件解密
    o.VerificationToken = "xxx";         // 回调来源校验
    o.EventRoute = "/feishu/events";     // 飞书后台填这个地址
});
```

发消息（三种方式）：

```csharp
await client.SendTextAsync("部署完成：v1.2.3");            // 群机器人
await client.SendCardAsync(new { ... });                  // 交互卡片
await client.SendToUserAsync(openId, "订单已发货");        // 单聊（需 im:message:send）

// 接入统一通知路由（渠道名 feishu，与 email 同一套写法）
builder.Services.AddLedonFeishuNotifications();
await sender.SendAsync(new Notification("feishu", openId, "告警", "CPU 90%"), ct);
```

收消息（事件回调）：

```csharp
public class MessageHandler : IFeishuEventHandler
{
    public Task HandleAsync(FeishuEvent @event, CancellationToken ct = default)
    {
        if (@event.EventType == "im.message.receive_v1")
        {
            var message = @event.Event.Deserialize<FeishuMessageEvent>();
            // message.Sender.SenderId.OpenId / message.Message.Content
        }
        return Task.CompletedTask;
    }
}
```

- URL 校验（`url_verification`）自动应答 `challenge`；
- 回调内容 AES-256-CBC 自动解密；Token 校验失败返回 401；
- 处理器异常只记录（飞书按 200 判定投递成功）；
- 自定义机器人签名 = `Base64(HMAC-SHA256(key: "{timestamp}\n{secret}", msg: ""))`；
- Lark 国际版把 `BaseUrl` 改成 `https://open.larksuite.com`。

## Ledon.Protocols —— 协议解析（单独库）

> 传输层是独立包：`Ledon.Sockets.Client` / `Ledon.Sockets.Server`。

```csharp
// 帧编解码（第一层：字节流 → 完整包）
IFrameCodec codec = new LengthPrefixedFrameCodec();     // [长度(4)][载荷]
IFrameCodec lines = new DelimiterFrameCodec((byte)'\n'); // 换行分隔
IFrameCodec fixedSize = new FixedSizeFrameCodec(32);     // 定长

// 包编解码（第二层：包 → 对象）
var binary = new BinaryPacketCodec(magic: 0x4C42, version: 1);   // 魔数/版本校验
var text = new TextPacketCodec();

// 结构体映射：一份结构体就是协议契约
public struct LoginRequest
{
    [PacketField(0, PacketFieldKind.UInt32)] public uint Version;
    [PacketField(1, PacketFieldKind.Utf8String)] public string? User;
}

var bytes = PacketSerializer<LoginRequest>.Serialize(new LoginRequest { Version = 3, User = "alice" });
var request = PacketSerializer<LoginRequest>.Deserialize(bytes);
```

- 每连接一个独立读取循环 + 独立 DI 作用域（`connection.Services` 拿 DbContext/仓储）；
- **半包自动拼接**、多帧一次读取全部分发、缓冲区溢出保护；
- 帧按到达顺序处理；单帧失败只记录不断开；`ProtocolException`（协议不同步）断开该连接；
- UDP 无连接，处理器并发调用（须线程安全）。

## Ledon.Sip —— SIP 协议解析（纯解析）

```csharp
var message = SipParser.Parse(text);   // 或 Parse(bytes)
message.Method;      // INVITE / ACK / BYE / REGISTER / OPTIONS
message.CallId;      // 对话标识
message.From; message.To; message.Via;
message.Body;        // 通常是 SDP

var sdp = SdpParser.Parse(message.Body);
sdp.Media[0].Media;      // audio / video
sdp.Media[0].Port;
sdp.Media[0].Formats;    // 编码/DTMF 事件号（0=PCMU, 101=telephone-event）

// 自定义应答：返回 null = 不应答
public class MySipHandler : ISipHandler
{
    public Task<SipMessage?> HandleAsync(SipMessage message, CancellationToken ct) { ... }
}
```

## Ledon.Sip.Server / Ledon.Sip.Client —— 基于 Socket 库

SIP 有自己的 Server/Client 包，**构建在 Socket 库之上**（解析在 `Ledon.Sip`，传输在 `Ledon.Sockets.*`）：

```csharp
// 服务端：UDP 监听 + 应答（也可 TCP + Content-Length 切帧）
builder.Services.AddLedonSipServer(o =>
{
    o.Port = 5060;
    o.AutoAnswerOptions = true;
});
builder.Services.AddSingleton<ISipHandler, MySipHandler>();

// 客户端：事务匹配 + 重传 + 超时
builder.Services.AddLedonSipClient(o => { o.Host = "pbx.example.com"; o.Port = 5060; });

var client = provider.GetRequiredService<SipClient>();
await client.ConnectAsync();
var response = await client.SendRequestAsync(request);   // 等最终响应（2xx-6xx）
```

- 服务端只依赖 `Ledon.Sockets.Server`（socket/作用域/并发）+ `Ledon.Sip`（解析/应答）；
- 客户端只依赖 `Ledon.Sockets.Client`（连接/重连）+ `Ledon.Sip`（解析/应答）；
- 客户端按 Via branch 匹配响应（RFC 3261），1xx 不完成事务，UDP 上指数退避重传；
- TCP 传输用 `SipTcpFrameCodec`（按 Content-Length 切帧，RFC 3261 §18.3）。

- RFC 3261 请求/状态行、头字段（大小写不敏感、同名多值拼接）、body；
- SDP 解析：会话名/连接信息/媒体流/载荷格式/属性；
- 序列化回线上字节（CRLF 行尾）；
- 解析失败只记录不崩（UDP 无连接，不能"断开"任何人）。

完整文档：<https://github.com/wosledon/ledon-framework>
