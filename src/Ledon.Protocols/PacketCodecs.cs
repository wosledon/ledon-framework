using System.Buffers.Binary;
using System.Text;

namespace Ledon.Protocols;

/// <summary>帧载荷 ↔ 对象 的编解码（协议解析的第二层）。</summary>
public interface IPacketCodec<TPacket>
{
    /// <summary>编码为帧载荷。</summary>
    byte[] Encode(TPacket packet);

    /// <summary>从帧载荷解码。</summary>
    TPacket Decode(ReadOnlySpan<byte> payload);
}

/// <summary>UTF-8 文本包编解码（一行一个指令的文本协议）。</summary>
public sealed class TextPacketCodec : IPacketCodec<string>
{
    public byte[] Encode(string packet) => Encoding.UTF8.GetBytes(packet);

    public string Decode(ReadOnlySpan<byte> payload) => Encoding.UTF8.GetString(payload);
}

/// <summary>
/// 结构化二进制包编解码：定长头部 + 载荷。
/// <code>
/// [魔数(2)][版本(1)][类型(1)][序列号(4)][载荷长度(4)][载荷]
/// </code>
/// 魔数/版本不匹配直接抛 <see cref="ProtocolException"/>——协议不同步要尽早发现。
/// </summary>
public sealed class BinaryPacketCodec(ushort magic, byte version) : IPacketCodec<BinaryPacket>
{
    private const int HeaderSize = 12;

    public ushort Magic { get; } = magic;

    public byte Version { get; } = version;

    public byte[] Encode(BinaryPacket packet)
    {
        var frame = new byte[HeaderSize + packet.Payload.Length];

        BinaryPrimitives.WriteUInt16LittleEndian(frame, Magic);
        frame[2] = Version;
        frame[3] = packet.Type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), packet.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), packet.Payload.Length);
        packet.Payload.CopyTo(frame.AsSpan(HeaderSize));

        return frame;
    }

    public BinaryPacket Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
            throw new ProtocolException($"包长 {payload.Length} 小于头部 {HeaderSize} 字节");

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (magic != Magic)
            throw new ProtocolException($"魔数不匹配（收到 0x{magic:X4}，期望 0x{Magic:X4}）");

        var version = payload[2];
        if (version != Version)
            throw new ProtocolException($"协议版本不匹配（收到 {version}，期望 {Version}）");

        var type = payload[3];
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);

        if (length < 0 || payload.Length - HeaderSize < length)
            throw new ProtocolException($"载荷长度字段非法（{length}，实际可用 {payload.Length - HeaderSize}）");

        return new BinaryPacket(type, sequence, payload.Slice(HeaderSize, length).ToArray());
    }
}

/// <summary>结构化二进制包。</summary>
/// <param name="Type">消息类型（业务自定义语义）。</param>
/// <param name="Sequence">序列号（请求/响应对齐、乱序检测）。</param>
/// <param name="Payload">载荷字节。</param>
public sealed record BinaryPacket(byte Type, uint Sequence, byte[] Payload);
