# Ledon.Protocols.Server

协议服务端：TCP/UDP 监听、连接管理、类型化分发。与 `Ledon.Protocols`（解析）、
`Ledon.Protocols.Client`（客户端）分开——按需引用，互不拖累。

```csharp
builder.Services.AddLedonProtocolServer(
    o => { o.Port = 9000; o.BufferSize = 8192; },
    new LengthPrefixedFrameCodec(),
    new MyHandler());
```

## 连接处理器

```csharp
public class MyHandler : ConnectionHandlerBase
{
    public override async Task OnConnectedAsync(ConnectionContext connection, CancellationToken ct)
    {
        // 登录态、限流计数都放 Items 里
        connection.Items["loginAt"] = DateTimeOffset.UtcNow;
        await connection.SendPacketAsync(new Welcome("hi"), ct);
    }

    public override async Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var packet = PacketSerializer<LoginRequest>.Deserialize(frame.Span);
        await connection.SendPacketAsync(new LoginReply(true), ct);
    }

    public override Task OnDisconnectedAsync(ConnectionContext connection, Exception? error, CancellationToken ct)
    {
        // 资源清理点（连接级状态在这里收尾）
        return Task.CompletedTask;
    }
}
```

- 每连接一个独立读取循环 + 独立 DI 作用域（`connection.Services` 拿 DbContext/仓储）；
- 帧按到达顺序处理；单帧处理失败只记录不断开；`ProtocolException` 断开该连接；
- 半包自动拼接；缓冲上限保护（默认 1MB，超过视为对端异常）；
- `connection.SendPacketAsync(packet)`：包编解码 + 帧编码一步完成。

## 类型化分发（按包类型路由）

```csharp
[PacketType(1)]
public struct LoginRequest { /* [PacketField] ... */ }

public class LoginRequestHandler : IPacketHandler<LoginRequest>
{
    public Task HandleAsync(ConnectionContext connection, LoginRequest packet, CancellationToken ct) { ... }
}

// 启动时
builder.Services.AddLedonProtocolServer(o => o.Port = 9000, new LengthPrefixedFrameCodec(), new LoggingConnectionHandler(...));
builder.Services.AddLedonPacketHandlers();   // 扫描 + 注册 + 编译解码委托
```

帧布局约定：前 2 字节 = 类型标识（小端），其余 = 包体。协议不同就自己实现 `IPacketDispatcher`。

## UDP 服务端

```csharp
builder.Services.AddLedonProtocolUdpServer(o => o.Port = 9001, new MyDatagramHandler());

public class MyDatagramHandler : IUdpHandler
{
    public Task OnDatagramAsync(UdpContext datagram, CancellationToken ct = default)
    {
        // UDP 无连接、无顺序保证：本方法并发调用，实现须线程安全
        return datagram.ReplyAsync(datagram.Data, ct);   // 回显
    }
}
```

完整文档：<https://github.com/wosledon/ledon-framework>
