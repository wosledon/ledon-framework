using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Ledon.Streaming.Media;

/// <summary>AMF0 标记位。</summary>
internal static class Amf0Marker
{
    public const byte Number = 0x00;
    public const byte Boolean = 0x01;
    public const byte String = 0x02;
    public const byte Object = 0x03;
    public const byte Null = 0x05;
    public const byte Undefined = 0x06;
    public const byte Reference = 0x07;
    public const byte EcmaArray = 0x08;
    public const byte ObjectEnd = 0x09;
    public const byte StrictArray = 0x0A;
    public const byte Date = 0x0B;
    public const byte LongString = 0x0C;
}

/// <summary>
/// AMF0 读写器（Action Message Format 0）：RTMP 的 connect/publish/play/onStatus
/// 等命令都用这种编码。按 RFC 手写，零依赖、零分配（除结果对象）。
/// </summary>
public ref struct Amf0Writer
{
    private readonly IBufferWriter<byte> _writer;

    public Amf0Writer(IBufferWriter<byte> writer) => _writer = writer;

    /// <summary>写字符串（2 字节长度前缀 + UTF-8）。</summary>
    public void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var span = _writer.GetSpan(3 + bytes.Length);
        span[0] = Amf0Marker.String;
        BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)bytes.Length);
        bytes.CopyTo(span[3..]);
        _writer.Advance(3 + bytes.Length);
    }

    /// <summary>写双精度浮点（9 字节）。</summary>
    public void WriteNumber(double value)
    {
        var span = _writer.GetSpan(9);
        span[0] = Amf0Marker.Number;
        BinaryPrimitives.WriteDoubleBigEndian(span[1..], value);
        _writer.Advance(9);
    }

    /// <summary>写布尔（2 字节）。</summary>
    public void WriteBoolean(bool value)
    {
        var span = _writer.GetSpan(2);
        span[0] = Amf0Marker.Boolean;
        span[1] = value ? (byte)1 : (byte)0;
        _writer.Advance(2);
    }

    /// <summary>写 Null（1 字节）。</summary>
    public void WriteNull()
    {
        _writer.GetSpan(1)[0] = Amf0Marker.Null;
        _writer.Advance(1);
    }

    /// <summary>写普通对象（含 3 字节 ObjectEnd 结束标记 + 空键）。</summary>
    public void WriteObjectStart() => WriteMarker(Amf0Marker.Object);

    /// <summary>写对象的一个属性（键 + 值）。</summary>
    public void WriteProperty(string name, double value)
    {
        WritePropertyName(name);
        WriteNumber(value);
    }

    /// <summary>写对象的一个字符串属性。</summary>
    public void WriteProperty(string name, string value)
    {
        WritePropertyName(name);
        WriteString(value);
    }

    /// <summary>写对象的一个布尔属性。</summary>
    public void WriteProperty(string name, bool value)
    {
        WritePropertyName(name);
        WriteBoolean(value);
    }

    /// <summary>结束对象（24 位 0 键 + ObjectEnd 标记）。</summary>
    public void WriteObjectEnd()
    {
        var span = _writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, Amf0Marker.ObjectEnd);
        _writer.Advance(4);
    }

    /// <summary>写 ECMA 数组（ associative 数组，键为字符串）。</summary>
    public void WriteEcmaArrayStart(int count)
    {
        var span = _writer.GetSpan(5);
        span[0] = Amf0Marker.EcmaArray;
        BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)count);
        _writer.Advance(5);
    }

    private void WritePropertyName(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var span = _writer.GetSpan(2 + bytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)bytes.Length);
        bytes.CopyTo(span[2..]);
        _writer.Advance(2 + bytes.Length);
    }

    private void WriteMarker(byte marker)
    {
        _writer.GetSpan(1)[0] = marker;
        _writer.Advance(1);
    }
}

/// <summary>
/// AMF0 读取器：按标记位逐个解析值（字符串/数字/布尔/Null/对象/ECMA 数组/严格数组）。
/// </summary>
public ref struct Amf0Reader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public Amf0Reader(ReadOnlySpan<byte> buffer) => _buffer = buffer;

    /// <summary>当前位置。</summary>
    public readonly int Position => _position;

    /// <summary>是否还有数据。</summary>
    public readonly bool HasMore => _position < _buffer.Length;

    /// <summary>读取下一个值（按标记位分发）。</summary>
    public object? ReadValue()
    {
        if (!HasMore)
            return null;

        var marker = _buffer[_position++];

        switch (marker)
        {
            case Amf0Marker.Number:
                var number = BinaryPrimitives.ReadDoubleBigEndian(Read(8));
                return number;

            case Amf0Marker.Boolean:
                return Read(1)[0] != 0;

            case Amf0Marker.String:
                return ReadString();

            case Amf0Marker.LongString:
                return ReadLongString();

            case Amf0Marker.Object:
                return ReadObject();

            case Amf0Marker.EcmaArray:
                Read(4);   // 近似长度，实际以 ObjectEnd 为准
                return ReadObject();

            case Amf0Marker.StrictArray:
                var count = BinaryPrimitives.ReadUInt32BigEndian(Read(4));
                var items = new object?[count];
                for (var i = 0; i < count; i++)
                    items[i] = ReadValue();
                return items;

            case Amf0Marker.Null:
            case Amf0Marker.Undefined:
                return null;

            default:
                throw new InvalidOperationException($"不支持的 AMF0 标记 0x{marker:X2}");
        }
    }

    /// <summary>读字符串（2 字节长度前缀）。</summary>
    public string ReadString()
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(Read(2));
        return Encoding.UTF8.GetString(Read(length));
    }

    private string ReadLongString()
    {
        var length = BinaryPrimitives.ReadUInt32BigEndian(Read(4));
        return Encoding.UTF8.GetString(Read((int)length));
    }

    private Dictionary<string, object?> ReadObject()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        while (HasMore)
        {
            var keyLength = BinaryPrimitives.ReadUInt16BigEndian(Read(2));
            if (keyLength == 0)
            {
                Read(1);   // ObjectEnd 标记
                break;
            }

            var key = Encoding.UTF8.GetString(Read(keyLength));
            result[key] = ReadValue();
        }

        return result;
    }

    private ReadOnlySpan<byte> Read(int count)
    {
        if (_position + count > _buffer.Length)
            throw new InvalidOperationException("AMF0 缓冲区不足");

        var span = _buffer.Slice(_position, count);
        _position += count;

        return span;
    }
}
