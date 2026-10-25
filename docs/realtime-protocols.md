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
- **发送串行 + 发送超时**：同一连接的并发写不会把帧交叉成乱码，慢读者不会挂住处理器；
- **空闲超时 + 连接数上限**：静默连接和洪水连接都不会耗尽文件描述符；
- 接受循环容错：单次 accept 失败只记录，监听器不会毫无提示地停摆；
- UDP 无连接，处理器并发调用（须线程安全）；派发是**有界并发**的，洪水数据报不会每个都起任务。

## Ledon.Sip —— SIP 协议解析（纯解析）

```csharp
var message = SipParser.Parse(text);   // 或 Parse(bytes)
message.Method;      // INVITE / ACK / BYE / REGISTER / OPTIONS
message.CallId;      // 对话标识
message.From; message.To; message.Via;

// 类型化头：不用自己切字符串
message.FromAddress?.Tag;        // From 的 tag
message.CSeqValue?.Number;       // CSeq 序号
message.CSeqValue?.Method;       // CSeq 方法
message.TopVia?.Branch;          // 事务键
message.TopVia?.Received;        // NAT 场景对端观察到的地址

// 同名多值保序（Via 的顺序就是响应的回程路径）
foreach (var via in message.HeaderValues("Via")) { ... }

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
// 服务端：UDP + TCP（默认都开），带完整事务层
builder.Services.AddLedonSipServer(o =>
{
    o.Port = 5060;
    o.TcpPort = 5060;      // TCP 与 UDP 同端口，符合规范做法
    o.AutoAnswerOptions = true;
});
builder.Services.AddSingleton<ISipHandler, MySipHandler>();

// 客户端：事务 + 对话 + ACK/CANCEL
builder.Services.AddLedonSipClient(o => { o.Host = "pbx.example.com"; o.Port = 5060; });

var client = provider.GetRequiredService<SipClient>();
await client.ConnectAsync();

// 只要方法和 URI：Via/branch/Call-ID/CSeq/Contact 全部自动生成
var response = await client.SendAsync("OPTIONS", "sip:pbx.example.com");

// INVITE：2xx 返回对话；非 2xx 客户端自动补 ACK（规范 17.1.1.2 强制）
var (dialog, inviteResponse) = await client.InviteAsync("sip:bob@example.com", sdp);
await client.AckAsync(request, inviteResponse);
await client.ByeAsync(dialog);
await client.CancelAsync(request);
```

- 服务端**事务层**：UDP 上按 T1 指数重发最终响应，直到收到 ACK 或 Timer H；
  同 branch 的重复请求重发上次的响应而不重复执行业务；
- 事务键 = `branch + 方法`：CANCEL 与它取消的 INVITE 共用 branch（规范 9.1），必须分开；
- 客户端按 Via branch + CSeq 方法匹配响应，1xx 不完成事务，UDP 上指数退避重传；
- 可靠传输（TCP/TLS）不重传，只等定时器；
- 响应自动带 To tag / Contact / Server / Content-Length——缺 To tag 的真实 UAC 会直接判为非法。

- RFC 3261 请求/状态行、头字段（大小写不敏感、同名多值保序）、body；
- 行 continuation（obs-fold，规范 7.3.1）解析时展开；
- 消息大小上限 + 必备头校验：缺头的消息没法回包，早失败比晚失败好排查；
- Content-Length 按 body 字节数自动计算（少写它，TCP 对端会一直等 body 收完）；
- SDP 解析：会话名/连接信息/媒体流/载荷格式/属性；
- 解析失败只记录不崩（UDP 无连接，不能"断开"任何人）。

完整文档：<https://github.com/wosledon/ledon-framework>
