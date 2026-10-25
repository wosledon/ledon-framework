# Ledon.Protocols

TCP/UDP 服务端 + 帧/包编解码：接硬件、接私有协议、接游戏/物联网网关，不用从零写粘包处理。

## 帧编解码（第一层：字节流 → 完整包）

```csharp
// 长度前缀（最通用）：[长度(4)][载荷]
IFrameCodec codec = new LengthPrefixedFrameCodec();

// 分隔符（文本协议）：按 0x0A 切行
IFrameCodec lines = new DelimiterFrameCodec((byte)'\n');

// 定长（老旧硬件）：每帧 32 字节
IFrameCodec fixedSize = new FixedSizeFrameCodec(32);
```

## 包编解码（第二层：包 → 对象）

```csharp
// 文本
var text = new TextPacketCodec();

// 结构化二进制：[魔数(2)][版本(1)][类型(1)][序列号(4)][长度(4)][载荷]
var binary = new BinaryPacketCodec(magic: 0x4C42, version: 1);
var frame = binary.Encode(new BinaryPacket(type: 1, sequence: 42, payload));
var packet = binary.Decode(frame);   // 魔数/版本不匹配抛 ProtocolException
```

## TCP 服务端

```csharp
builder.Services.AddLedonTcpServer(
    o => { o.Port = 9000; o.BufferSize = 8192; },
    new LengthPrefixedFrameCodec(),
    new MyHandler());   // IConnectionHandler：只重写关心的事件

public class MyHandler : ConnectionHandlerBase
{
    private readonly BinaryPacketCodec _codec = new(0x4C42, 1);

    public override async Task OnFrameAsync(ConnectionContext connection, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var packet = _codec.Decode(frame.Span);

        if (packet.Type == 1)   // 回显
            await connection.SendAsync(_codec.Encode(new BinaryPacket(1, packet.Sequence, packet.Payload)), ct);
    }
}
```

- 每连接一个独立读取循环 + 独立 DI 作用域（`connection.Services` 拿 DbContext/仓储）；
- 帧按到达顺序处理；单帧处理失败只记录不断开；`ProtocolException`（协议不同步）断开该连接；
- 半包自动拼接：一次 `ReadAsync` 没收全的帧会等下次数据到达；
- 缓冲区溢出保护（对端持续发不完整帧时断开而不是 OOM）。

## UDP 端点

```csharp
builder.Services.AddLedonUdpEndpoint(o => o.Port = 9001, new MyDatagramHandler());

public class MyDatagramHandler : IUdpHandler
{
    public Task OnDatagramAsync(UdpContext datagram, CancellationToken ct = default)
    {
        // UDP 无连接、无顺序保证：本方法并发调用，实现须线程安全
        return Task.CompletedTask;
    }
}
```

SIP（RFC 3261）消息解析见 `Ledon.Protocols.Sip`。

完整文档：<https://github.com/wosledon/ledon-framework>
