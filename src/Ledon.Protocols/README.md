# Ledon.Protocols

协议解析库（零依赖）：帧编解码、二进制读写、**结构体映射**。
客户端与服务端是独立包：`Ledon.Protocols.Client` / `Ledon.Protocols.Server`。

## 结构体映射（核心）

```csharp
public struct LoginRequest
{
    [PacketField(0, PacketFieldKind.UInt32)] public uint Version;
    [PacketField(1, PacketFieldKind.Utf8String)] public string? User;
    [PacketField(2, PacketFieldKind.Bool)] public bool Remember;
}

var bytes = PacketSerializer<LoginRequest>.Serialize(new LoginRequest { Version = 3, User = "alice", Remember = true });
var request = PacketSerializer<LoginRequest>.Deserialize(bytes);
```

**布局即契约**：两端各写一份结构体就能通信，不需要 IDL / schema 文件。

性能设计：
- 首次使用时为每个成员**编译强类型访问器**（表达式树），之后零反射、零装箱；
- 读写在 `PacketReader` / `PacketWriter`（ref struct）上进行，无中间数组；
- 定长布局可预计算大小（`PacketSerializer<T>.Size`），变长布局用 `Measure`；
- 实测单次解析几十纳秒级（测试里有性能守卫，回归成反射会直接失败）。

支持的字段类型：`Bool` `Int8/UInt8` `Int16/UInt16` `Int32/UInt32` `Int64/UInt64`
`Float32` `Double` `Utf8String` `FixedString` `Bytes` `FixedBytes` `Struct`（嵌套）
`Array`（数组）；枚举按底层类型走。默认小端，`bigEndian: true` 或字段级
`[PacketField(0, Kind = PacketFieldKind.Int32, BigEndian = true)]` 切大端。

## 帧编解码（字节流 → 完整包）

```csharp
IFrameCodec codec = new LengthPrefixedFrameCodec();      // [长度(4)][载荷]
IFrameCodec lines = new DelimiterFrameCodec((byte)'\n'); // 换行分隔（也支持 "\r\n"）
IFrameCodec fixedSize = new FixedSizeFrameCodec(32);     // 定长
```

## 二进制读写（手动场景）

```csharp
var buffer = new ArrayBufferWriter<byte>();
var writer = new PacketWriter(buffer, bigEndian: true);
writer.WriteUInt32(0x11223344);
writer.WriteUtf8String("hi");

var reader = new PacketReader(buffer.WrittenSpan, bigEndian: true);
var magic = reader.ReadUInt32();
var text = reader.ReadUtf8String();
```

协议不同步/帧损坏抛 `ProtocolException`（与普通 IO 错误区分，调用方可决定是否断开连接）。

完整文档：<https://github.com/wosledon/ledon-framework>
