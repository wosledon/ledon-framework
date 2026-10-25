# Ledon.Protocols.Sip

SIP（RFC 3261）消息解析 + SDP 解析 + 自动应答端点。接 IP 电话、软电话、SIP 网关、语音机器人。

```csharp
builder.Services.AddLedonSip(o =>
{
    o.Port = 5060;                 // UDP 监听端口
    o.UserAgent = "Ledon/1.0";
    o.AutoAnswerOptions = true;    // OPTIONS 保活自动 200 OK
});
```

## 解析

```csharp
var message = SipParser.Parse(text);   // 或 SipParser.Parse(bytes)

message.Type;          // Request / Response
message.Method;        // INVITE / ACK / BYE / REGISTER / OPTIONS / CANCEL
message.RequestUri;    // sip:bob@example.com
message.StatusCode;    // 响应状态码（200/404/486……）
message.CallId;        // 对话标识
message.From;          // From 头（含 tag）
message.To;
message.Via;
message.Body;          // 通常是 SDP

// 序列化回线上字节（CRLF 行尾，符合 RFC 3261）
var bytes = message.ToBytes();
```

SDP 解析：

```csharp
var sdp = SdpParser.Parse(message.Body);
sdp.SessionName;      // 会话名
sdp.Connection;       // 连接信息（IN IP4 x.x.x.x）
sdp.Media[0].Media;   // audio / video
sdp.Media[0].Port;
sdp.Media[0].Formats; // 编码/DTMF 事件号（0=PCMU, 101=telephone-event）
```

## 自定义处理

```csharp
public class MySipHandler : ISipHandler
{
    public async Task<SipMessage?> HandleAsync(SipMessage message, SipEndpoint endpoint, CancellationToken ct)
    {
        if (message.Method == "INVITE")
        {
            var sdp = SdpParser.Parse(message.Body);
            // 接听逻辑……
            return SipResponder.Ok(message, "Ledon/1.0");
        }

        return null;   // 不应答
    }
}
```

- 头字段大小写不敏感；同名多值（Via/Route）按顺序拼接；
- 解析失败只记录不崩：UDP 无连接，不能"断开"任何人；
- 默认应答器只处理保活与（可选）注册；INVITE/BYE 等业务语义由你的处理器决定。

完整文档：<https://github.com/wosledon/ledon-framework>
