# Ledon.Sip.Server

SIP 服务端：**基于 `Ledon.Sockets.Server` 的 socket 库**构建，协议解析用 `Ledon.Sip`。

```csharp
builder.Services.AddLedonSipServer(o =>
{
    o.Port = 5060;                 // 标准 SIP 端口
    o.AutoAnswerOptions = true;    // OPTIONS 保活自动 200 OK
});

// 业务处理器：实现 ISipHandler（Ledon.Sip 提供）
public class MySipHandler : ISipHandler
{
    public Task<SipMessage?> HandleAsync(SipMessage request, CancellationToken ct = default)
    {
        if (request.Method == "INVITE")
        {
            var sdp = SdpParser.Parse(request.Body);
            // 接听逻辑……
            return Task.FromResult<SipMessage?>(SipResponder.Ok(request, "Ledon/1.0"));
        }

        return Task.FromResult<SipMessage?>(null);   // 不应答
    }
}
```

- 传输层（socket/作用域/并发）由 `Ledon.Sockets.Server` 负责：UDP 数据报并发处理、每数据报独立 DI 作用域；
- 协议层（解析/应答）由 `Ledon.Sip` 负责：`SipParser` + `SipResponder`；
- 本包只是约 40 行的适配器（`SipUdpHandler : IUdpHandler`）；
- TCP 传输用 `SipTcpFrameCodec`（按 Content-Length 切帧，RFC 3261 §18.3）；
- 解析失败只记录不崩（UDP 无连接，不能"断开"任何人）。

完整文档：<https://github.com/wosledon/ledon-framework>
