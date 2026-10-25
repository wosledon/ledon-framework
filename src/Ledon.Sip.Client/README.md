# Ledon.Sip.Client

SIP 客户端（事务用户）：**基于 `Ledon.Sockets.Client` 的 socket 库**构建，协议解析用 `Ledon.Sip`。

```csharp
builder.Services.AddLedonSipClient(o =>
{
    o.Host = "pbx.example.com";
    o.Port = 5060;
});

var client = provider.GetRequiredService<SipClient>();
await client.ConnectAsync();

var response = await client.SendRequestAsync(request);   // 等最终响应（2xx-6xx）
```

请求需要带 Via 的 branch 参数（事务匹配的依据）：

```csharp
var request = SipParser.CreateRequest("OPTIONS", "sip:pbx.example.com", new Dictionary<string, string>
{
    ["Via"] = $"SIP/2.0/UDP 127.0.0.1;branch=z9hG4bK-{Guid.NewGuid():N}",
    ["From"] = "<sip:me@example.com>;tag=1",
    ["To"] = "<sip:pbx.example.com>",
    ["Call-ID"] = $"{Guid.NewGuid():N}@example.com",
    ["CSeq"] = "1 OPTIONS",
});
```

- 事务匹配按 Via branch（RFC 3261 的规定做法）；
- 临时响应（1xx）不完成事务，只记日志；
- UDP 上指数退避重传（默认 500ms 起，最多 7 次）；
- 超时抛 `TimeoutException`（默认 32 秒 = Timer B）；
- 传输层（连接/重连/半包拼接）由 `Ledon.Sockets.Client` 负责。

完整文档：<https://github.com/wosledon/ledon-framework>
