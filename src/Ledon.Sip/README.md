# Ledon.Sip

SIP（RFC 3261）与 SDP（RFC 4566）**协议解析**——只依赖 `Ledon.Protocols`，零 socket 代码。
服务端与客户端是独立包：`Ledon.Sip.Server` / `Ledon.Sip.Client`（都构建在 `Ledon.Sockets.*` 之上）。

## 解析

```csharp
var message = SipParser.Parse(text);   // 或 SipParser.Parse(bytes)

message.Type;          // Request / Response
message.Method;        // INVITE / ACK / BYE / REGISTER / OPTIONS
message.RequestUri;    // sip:bob@example.com
message.StatusCode;    // 响应状态码（200/404/486……）
message.CallId;        // 对话标识
message.From; message.To; message.Via;
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

- 头字段大小写不敏感；同名多值（Via/Route）按顺序拼接；
- 构造响应：`SipResponder.Ok(request, userAgent)` 自动带上 Via/From/To/Call-ID/CSeq；
- 自定义应答策略：实现 `ISipHandler`（返回 `null` = 不应答）。

完整文档：<https://github.com/wosledon/ledon-framework>
