using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ledon.Protocols;

/// <summary>
/// 二进制写入器（ref struct，零分配到托管堆）：顺序写入 <see cref="IBufferWriter{T}"/>，
/// 由调用方决定落到数组池、管道还是栈缓冲。
/// </summary>
public ref struct PacketWriter
{
    private readonly IBufferWriter<byte> _writer;
    private readonly bool _bigEndian;

    public PacketWriter(IBufferWriter<byte> writer, bool bigEndian = false)
    {
        _writer = writer;
        _bigEndian = bigEndian;
    }

    /// <summary>写入端序（构造时指定；字段级 <see cref="PacketFieldAttribute.BigEndian"/> 可再强制大端）。</summary>
    public readonly bool BigEndian => _bigEndian;

    /// <summary>已写入字节数。</summary>
    public int Written { get; private set; }
    /// <summary>重置写入器（配合自管理缓冲区使用）。</summary>
    public void Reset()
    {
        Written = 0;
    }

    public void WriteByte(byte value) => Write(value);

    public void WriteSByte(sbyte value) => Write((byte)value);

    public void WriteBool(bool value) => Write(value ? (byte)1 : (byte)0);

    public void WriteInt16(short value) => WriteInt16(value, _bigEndian);

    public void WriteInt16(short value, bool bigEndian)
    {
        var span = _writer.GetSpan(2);
        if (bigEndian)
            BinaryPrimitives.WriteInt16BigEndian(span, value);
        else
            BinaryPrimitives.WriteInt16LittleEndian(span, value);
        _writer.Advance(2);
        Written += 2;
    }

    public void WriteUInt16(ushort value) => WriteUInt16(value, _bigEndian);

    public void WriteUInt16(ushort value, bool bigEndian)
    {
        var span = _writer.GetSpan(2);
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(span, value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        _writer.Advance(2);
        Written += 2;
    }

    public void WriteInt32(int value) => WriteInt32(value, _bigEndian);

    public void WriteInt32(int value, bool bigEndian)
    {
        var span = _writer.GetSpan(4);
        if (bigEndian)
            BinaryPrimitives.WriteInt32BigEndian(span, value);
        else
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
        _writer.Advance(4);
        Written += 4;
    }

    public void WriteUInt32(uint value) => WriteUInt32(value, _bigEndian);

    public void WriteUInt32(uint value, bool bigEndian)
    {
        var span = _writer.GetSpan(4);
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(span, value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        _writer.Advance(4);
        Written += 4;
    }

    public void WriteInt64(long value) => WriteInt64(value, _bigEndian);

    public void WriteInt64(long value, bool bigEndian)
    {
        var span = _writer.GetSpan(8);
        if (bigEndian)
            BinaryPrimitives.WriteInt64BigEndian(span, value);
        else
            BinaryPrimitives.WriteInt64LittleEndian(span, value);
        _writer.Advance(8);
        Written += 8;
    }

    public void WriteUInt64(ulong value) => WriteUInt64(value, _bigEndian);

    public void WriteUInt64(ulong value, bool bigEndian)
    {
        var span = _writer.GetSpan(8);
        if (bigEndian)
            BinaryPrimitives.WriteUInt64BigEndian(span, value);
        else
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        _writer.Advance(8);
        Written += 8;
    }

    public void WriteFloat32(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));

    public void WriteDouble(double value) => WriteUInt64(BitConverter.DoubleToUInt64Bits(value));

    /// <summary>写 2 字节长度前缀的 UTF-8 字符串。</summary>
    public void WriteUtf8String(string? value)
    {
        var byteCount = value is null ? 0 : Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ProtocolException($"字符串过长（{byteCount} 字节 > {ushort.MaxValue}）");

        WriteUInt16((ushort)byteCount);

        if (byteCount == 0)
            return;

        var span = _writer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        _writer.Advance(byteCount);
        Written += byteCount;
    }

    /// <summary>写指定编码、2 字节长度前缀的字符串。</summary>
    public void WriteString(string? value, PacketStringEncoding encoding)
    {
        var encoder = Encoder(encoding);
        var byteCount = value is null ? 0 : encoder.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ProtocolException($"字符串过长（{byteCount} 字节 > {ushort.MaxValue}）");

        WriteUInt16((ushort)byteCount);

        if (byteCount == 0)
            return;

        var span = _writer.GetSpan(byteCount);
        encoder.GetBytes(value.AsSpan(), span);
        _writer.Advance(byteCount);
        Written += byteCount;
    }

    /// <summary>写定长字符串（不足补 0，超出抛异常——布局即契约，超长说明契约被破坏）。</summary>
    public void WriteFixedString(string? value, int byteLength, PacketStringEncoding encoding = PacketStringEncoding.Utf8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);

        var encoder = Encoder(encoding);
        var byteCount = value is null ? 0 : encoder.GetByteCount(value);
        if (byteCount > byteLength)
            throw new ProtocolException($"字符串超出定长 {byteLength} 字节（实际 {byteCount}）");

        var span = _writer.GetSpan(byteLength);
        span[..byteLength].Clear();

        if (byteCount > 0)
            encoder.GetBytes(value.AsSpan(), span);

        _writer.Advance(byteLength);
        Written += byteLength;
    }

    /// <summary>写 2 字节长度前缀的字节数组。</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
            throw new ProtocolException($"字节数组过长（{value.Length} > {ushort.MaxValue}）");

        WriteUInt16((ushort)value.Length);
        WriteRaw(value);
    }

    /// <summary>写原始字节（无前缀）。</summary>
    public void WriteRaw(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;

        value.CopyTo(_writer.GetSpan(value.Length));
        _writer.Advance(value.Length);
        Written += value.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write(byte value)
    {
        _writer.GetSpan(1)[0] = value;
        _writer.Advance(1);
        Written += 1;
    }

    private static Encoding Encoder(PacketStringEncoding encoding) => encoding switch
    {
        PacketStringEncoding.Ascii => Encoding.ASCII,
        PacketStringEncoding.Utf16 => Encoding.Unicode,
        _ => Encoding.UTF8,
    };
}
