using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ledon.Protocols;

/// <summary>
/// 二进制读取器（ref struct，零分配）：在 <see cref="ReadOnlySpan{T}"/> 上顺序读取，
/// 位置前移由结构体自身维护。端序按调用处指定（默认小端）。
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly bool _bigEndian;
    private int _position;

    public PacketReader(ReadOnlySpan<byte> buffer, bool bigEndian = false)
    {
        _buffer = buffer;
        _bigEndian = bigEndian;
        _position = 0;
    }

    /// <summary>当前读位置。</summary>
    public readonly int Position => _position;

    /// <summary>剩余可读字节数。</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>底层缓冲区。</summary>
    public readonly ReadOnlySpan<byte> Buffer => _buffer;

    /// <summary>读取端序（构造时指定；字段级 <see cref="PacketFieldAttribute.BigEndian"/> 可再强制大端）。</summary>
    public readonly bool BigEndian => _bigEndian;

    /// <summary>是否还有数据可读。</summary>
    public readonly bool HasMore => _position < _buffer.Length;

    /// <summary>把读位置设为指定值（用于跳过或重读）。</summary>
    public void Seek(int position) => _position = position;

    /// <summary>跳过 n 字节。</summary>
    public void Skip(int count) => _position += count;

    public byte ReadByte()
    {
        Ensure(1);
        return _buffer[_position++];
    }

    public sbyte ReadSByte()
    {
        Ensure(1);
        return (sbyte)_buffer[_position++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public short ReadInt16() => ReadInt16(_bigEndian);

    public short ReadInt16(bool bigEndian)
    {
        Ensure(2);
        var value = bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(_buffer.Slice(_position, 2))
            : BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position, 2));
        _position += 2;

        return value;
    }

    public ushort ReadUInt16() => ReadUInt16(_bigEndian);

    public ushort ReadUInt16(bool bigEndian)
    {
        Ensure(2);
        var value = bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_position, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position, 2));
        _position += 2;

        return value;
    }

    public int ReadInt32() => ReadInt32(_bigEndian);

    public int ReadInt32(bool bigEndian)
    {
        Ensure(4);
        var value = bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(_buffer.Slice(_position, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
        _position += 4;

        return value;
    }

    public uint ReadUInt32() => ReadUInt32(_bigEndian);

    public uint ReadUInt32(bool bigEndian)
    {
        Ensure(4);
        var value = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_position, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, 4));
        _position += 4;

        return value;
    }

    public long ReadInt64() => ReadInt64(_bigEndian);

    public long ReadInt64(bool bigEndian)
    {
        Ensure(8);
        var value = bigEndian
            ? BinaryPrimitives.ReadInt64BigEndian(_buffer.Slice(_position, 8))
            : BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
        _position += 8;

        return value;
    }

    public ulong ReadUInt64() => ReadUInt64(_bigEndian);

    public ulong ReadUInt64(bool bigEndian)
    {
        Ensure(8);
        var value = bigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(_buffer.Slice(_position, 8))
            : BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, 8));
        _position += 8;

        return value;
    }

    public float ReadFloat32() => ReadFloat32(_bigEndian);

    public float ReadFloat32(bool bigEndian)
    {
        var bits = ReadUInt32(bigEndian);

        return BitConverter.UInt32BitsToSingle(bits);
    }

    public double ReadDouble() => ReadDouble(_bigEndian);

    public double ReadDouble(bool bigEndian)
    {
        var bits = ReadUInt64(bigEndian);

        return BitConverter.UInt64BitsToDouble(bits);
    }

    /// <summary>读 2 字节长度前缀的 UTF-8 字符串。</summary>
    public string ReadUtf8String()
    {
        var length = ReadUInt16();

        return Encoding.UTF8.GetString(ReadBytes(length));
    }

    /// <summary>读指定编码、2 字节长度前缀的字符串。</summary>
    public string ReadString(PacketStringEncoding encoding)
    {
        var length = ReadUInt16();

        return Decode(ReadBytes(length), encoding);
    }

    /// <summary>读定长字符串（不足补 0，尾部 0 截断）。</summary>
    public string ReadFixedString(int byteLength, PacketStringEncoding encoding = PacketStringEncoding.Utf8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);

        Ensure(byteLength);
        var span = _buffer.Slice(_position, byteLength);
        _position += byteLength;

        var end = span.IndexOf((byte)0);
        if (end >= 0)
            span = span[..end];

        return Decode(span, encoding);
    }

    /// <summary>读 2 字节长度前缀的字节数组。</summary>
    public byte[] ReadBytes()
    {
        var length = ReadUInt16();

        return ReadBytes(length);
    }

    /// <summary>读指定长度的字节数组。</summary>
    public byte[] ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Ensure(count);
        var result = _buffer.Slice(_position, count).ToArray();
        _position += count;

        return result;
    }

    /// <summary>读定长字节（不拷贝，直接返回底层切片）。</summary>
    public ReadOnlySpan<byte> ReadSpan(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Ensure(count);
        var span = _buffer.Slice(_position, count);
        _position += count;

        return span;
    }

    private static string Decode(ReadOnlySpan<byte> bytes, PacketStringEncoding encoding) => encoding switch
    {
        PacketStringEncoding.Ascii => Encoding.ASCII.GetString(bytes),
        PacketStringEncoding.Utf16 => Encoding.Unicode.GetString(bytes),
        _ => Encoding.UTF8.GetString(bytes),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int count)
    {
        if (_position + count > _buffer.Length)
            throw new ProtocolException($"缓冲区不足：需要 {count} 字节，剩余 {Remaining}");
    }
}
