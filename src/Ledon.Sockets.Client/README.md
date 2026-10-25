# Ledon.Sockets.Client

协议客户端：TCP/UDP 连接、断线自动重连、类型化收发。与 `Ledon.Protocols`（解析）、
`Ledon.Sockets.Server`（服务端）分开——按需引用。

```csharp
builder.Services.AddLedonSocketClient(
    o =>
    {
        o.Host = "127.0.0.1";
        o.Port = 9000;
        o.AutoReconnect = true;      // 断线后按 ReconnectDelay 重连
        o.ReconnectDelay = TimeSpan.FromSeconds(2);
    },
    new LengthPrefixedFrameCodec(),
    new MyClientHandler());
```

## 客户端处理器

```csharp
public class MyClientHandler : ClientHandlerBase
{
    private readonly IProtocolClient _client;

    public MyClientHandler(IProtocolClient client) => _client = client;

    public override async Task OnConnectedAsync(CancellationToken ct)
    {
        // 登录握手：连上就发
        await _client.SendPacketAsync(new LoginRequest { User = "alice" }, ct);
    }

    public override Task OnFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var reply = PacketSerializer<LoginReply>.Deserialize(frame.Span);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? error, CancellationToken ct)
    {
        // 断线清理（重连由框架负责）
        return Task.CompletedTask;
    }
}
```

- 半包自动拼接；缓冲上限保护（默认 1MB）；
- `SendPacketAsync(packet)`：包编码 + 帧编码一步完成；
- TCP 客户端随宿主启动即连、停机即断；连接失败按间隔重试；
- 手动控制（不随宿主）：直接 `new TcpProtocolClient(...)`，自己管 `ConnectAsync`/`CloseAsync`。

## UDP 客户端

```csharp
builder.Services.AddLedonSocketUdpClient(
    o => { o.Host = "127.0.0.1"; o.Port = 9001; },
    codec,
    new MyClientHandler());
```

完整文档：<https://github.com/wosledon/ledon-framework>
