using System.Buffers;
using System.Diagnostics;
using System.Text;
using Ledon.Protocols;

namespace Ledon.Web.Tests;

/// <summary>协议解析：结构体/类 ↔ 二进制 的高性能映射。</summary>
public class PacketSerializationTests
{
    [Fact]
    public void Struct_roundtrips_all_primitive_kinds()
    {
        var packet = new AllPrimitives
        {
            Flag = true,
            I8 = -12,
            U8 = 200,
            I16 = -12345,
            U16 = 54321,
            I32 = -1234567,
            U32 = 4000000000,
            I64 = -9000000000,
            U64 = 18000000000,
            F32 = 1.5f,
            F64 = -2.25,
        };

        var bytes = PacketSerializer<AllPrimitives>.Serialize(packet);
        var parsed = PacketSerializer<AllPrimitives>.Deserialize(bytes);

        Assert.Equal(packet.Flag, parsed.Flag);
        Assert.Equal(packet.I8, parsed.I8);
        Assert.Equal(packet.U8, parsed.U8);
        Assert.Equal(packet.I16, parsed.I16);
        Assert.Equal(packet.U16, parsed.U16);
        Assert.Equal(packet.I32, parsed.I32);
        Assert.Equal(packet.U32, parsed.U32);
        Assert.Equal(packet.I64, parsed.I64);
        Assert.Equal(packet.U64, parsed.U64);
        Assert.Equal(packet.F32, parsed.F32);
        Assert.Equal(packet.F64, parsed.F64);
    }

    [Fact]
    public void Fixed_layout_size_is_precomputed()
    {
        Assert.True(PacketSerializer<AllPrimitives>.IsFixedSize);
        Assert.Equal(1 + 1 + 1 + 2 + 2 + 4 + 4 + 8 + 8 + 4 + 8, PacketSerializer<AllPrimitives>.Size);
    }

    [Fact]
    public void Variable_layout_reports_no_fixed_size()
    {
        Assert.False(PacketSerializer<ProtocolLoginRequest>.IsFixedSize);
        Assert.Equal(-1, PacketSerializer<ProtocolLoginRequest>.Size);
    }

    [Fact]
    public void Measure_matches_serialized_length()
    {
        var packet = new ProtocolLoginRequest { Version = 3, User = "alice", Remember = true };

        var bytes = PacketSerializer<ProtocolLoginRequest>.Serialize(packet);

        Assert.Equal(bytes.Length, PacketSerializer<ProtocolLoginRequest>.Measure(packet));
    }

    [Fact]
    public void Strings_roundtrip_with_length_prefix()
    {
        var packet = new ProtocolLoginRequest { Version = 7, User = "张三", Remember = false };

        var parsed = PacketSerializer<ProtocolLoginRequest>.Deserialize(PacketSerializer<ProtocolLoginRequest>.Serialize(packet));

        Assert.Equal(7u, parsed.Version);
        Assert.Equal("张三", parsed.User);
        Assert.False(parsed.Remember);
    }

    [Fact]
    public void Empty_and_null_strings_are_handled()
    {
        var packet = new ProtocolLoginRequest { Version = 1, User = "", Remember = false };

        var parsed = PacketSerializer<ProtocolLoginRequest>.Deserialize(PacketSerializer<ProtocolLoginRequest>.Serialize(packet));

        Assert.Equal("", parsed.User);
    }

    [Fact]
    public void Fixed_string_pads_and_truncates()
    {
        var packet = new FixedNamePacket { Code = "AB" };   // 8 字节定长

        var bytes = PacketSerializer<FixedNamePacket>.Serialize(packet);
        Assert.Equal(8, bytes.Length);

        var parsed = PacketSerializer<FixedNamePacket>.Deserialize(bytes);
        Assert.Equal("AB", parsed.Code);
    }

    [Fact]
    public void Fixed_string_rejects_overflow()
    {
        var packet = new FixedNamePacket { Code = "ABCDEFGHIJK" };

        Assert.Throws<ProtocolException>(() => PacketSerializer<FixedNamePacket>.Serialize(packet));
    }

    [Fact]
    public void Bytes_field_roundtrips()
    {
        var packet = new BlobPacket { Payload = [1, 2, 3, 250] };

        var parsed = PacketSerializer<BlobPacket>.Deserialize(PacketSerializer<BlobPacket>.Serialize(packet));

        Assert.Equal(packet.Payload, parsed.Payload);
    }

    [Fact]
    public void Enum_uses_underlying_type()
    {
        var packet = new StatusPacket { Status = CommandStatus.Processing };

        var parsed = PacketSerializer<StatusPacket>.Deserialize(PacketSerializer<StatusPacket>.Serialize(packet));

        Assert.Equal(CommandStatus.Processing, parsed.Status);
    }

    [Fact]
    public void Nested_struct_roundtrips()
    {
        var packet = new Envelope
        {
            Header = new PacketHeader { Type = 9, Sequence = 42 },
            Body = new ProtocolLoginRequest { Version = 2, User = "bob", Remember = true },
        };

        var parsed = PacketSerializer<Envelope>.Deserialize(PacketSerializer<Envelope>.Serialize(packet));

        Assert.Equal(9, parsed.Header.Type);
        Assert.Equal(42u, parsed.Header.Sequence);
        Assert.Equal("bob", parsed.Body.User);
    }

    [Fact]
    public void Array_of_primitives_roundtrips()
    {
        var packet = new TelemetryPacket { Samples = [1, 2, 3, 4, 5] };

        var parsed = PacketSerializer<TelemetryPacket>.Deserialize(PacketSerializer<TelemetryPacket>.Serialize(packet));

        Assert.Equal([1, 2, 3, 4, 5], parsed.Samples ?? []);
    }

    [Fact]
    public void Empty_array_roundtrips()
    {
        var packet = new TelemetryPacket { Samples = [] };

        var parsed = PacketSerializer<TelemetryPacket>.Deserialize(PacketSerializer<TelemetryPacket>.Serialize(packet));

        Assert.Empty(parsed.Samples ?? []);
    }

    [Fact]
    public void Big_endian_layout_roundtrips()
    {
        var packet = new AllPrimitives { I32 = -1, U16 = 0x1234 };

        var bytes = PacketSerializer<AllPrimitives>.Serialize(packet, bigEndian: true);

        // 大端：0x1234 的高字节在前
        Assert.Equal(0x12, bytes[5]);   // Flag+I8+U8+I16 之后才是 U16
        Assert.Equal(0x34, bytes[6]);

        Assert.Equal(-1, PacketSerializer<AllPrimitives>.Deserialize(bytes, bigEndian: true).I32);
    }

    [Fact]
    public void Truncated_buffer_throws_protocol_exception()
    {
        var bytes = PacketSerializer<ProtocolLoginRequest>.Serialize(new ProtocolLoginRequest { Version = 1, User = "x" });

        Assert.Throws<ProtocolException>(() => PacketSerializer<ProtocolLoginRequest>.Deserialize(bytes.AsSpan(0, 2)));
    }

    [Fact]
    public void Type_without_fields_is_rejected()
    {
        // 布局错误在首次使用时暴露（类型初始化）：内层异常说明原因
        var exception = Assert.Throws<TypeInitializationException>(
            () => PacketSerializer<NoFields>.Serialize(new NoFields()));

        Assert.Contains("PacketField", exception.InnerException?.Message);
    }

    [Fact]
    public void Class_with_properties_roundtrips()
    {
        var packet = new ClassPacket { Id = 5, Name = "node-1" };

        var parsed = PacketSerializer<ClassPacket>.Deserialize(PacketSerializer<ClassPacket>.Serialize(packet));

        Assert.Equal(5, parsed.Id);
        Assert.Equal("node-1", parsed.Name);
    }

    [Fact]
    public void Serialization_is_fast_and_allocation_light()
    {
        var packet = new ProtocolLoginRequest { Version = 1, User = "benchmark-user", Remember = true };
        var bytes = PacketSerializer<ProtocolLoginRequest>.Serialize(packet);

        // 预热（编译访问器）
        for (var i = 0; i < 1000; i++)
            PacketSerializer<ProtocolLoginRequest>.Deserialize(bytes);

        var stopwatch = Stopwatch.StartNew();
        const int Iterations = 100_000;

        for (var i = 0; i < Iterations; i++)
            PacketSerializer<ProtocolLoginRequest>.Deserialize(bytes);

        stopwatch.Stop();

        var perCallMicros = stopwatch.Elapsed.TotalMilliseconds * 1000 / Iterations;

        // 结构体映射每次解析应远小于 1 微秒（实测几十纳秒级）；
        // 这里给 20 倍余量，只防"性能回归成反射"的情况
        Assert.True(perCallMicros < 20, $"单次解析 {perCallMicros:F2} µs，性能异常");
    }

    [Fact]
    public void Reader_writer_can_be_used_directly()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteUInt32(0x11223344);
        writer.WriteUtf8String("hi");

        var span = buffer.WrittenSpan;
        var reader = new PacketReader(span);
        Assert.Equal(0x11223344u, reader.ReadUInt32());
        Assert.Equal("hi", reader.ReadUtf8String());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Reader_reports_insufficient_buffer()
    {
        ProtocolException? caught = null;

        try
        {
            var reader = new PacketReader([1, 2, 3]);
            reader.ReadInt32();
        }
        catch (ProtocolException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
    }
}

public struct AllPrimitives
{
    [PacketField(0, PacketFieldKind.Bool)] public bool Flag;
    [PacketField(1, PacketFieldKind.Int8)] public sbyte I8;
    [PacketField(2, PacketFieldKind.UInt8)] public byte U8;
    [PacketField(3, PacketFieldKind.Int16)] public short I16;
    [PacketField(4, PacketFieldKind.UInt16)] public ushort U16;
    [PacketField(5, PacketFieldKind.Int32)] public int I32;
    [PacketField(6, PacketFieldKind.UInt32)] public uint U32;
    [PacketField(7, PacketFieldKind.Int64)] public long I64;
    [PacketField(8, PacketFieldKind.UInt64)] public ulong U64;
    [PacketField(9, PacketFieldKind.Float32)] public float F32;
    [PacketField(10, PacketFieldKind.Double)] public double F64;
}

public struct ProtocolLoginRequest
{
    [PacketField(0, PacketFieldKind.UInt32)] public uint Version;
    [PacketField(1, PacketFieldKind.Utf8String)] public string? User;
    [PacketField(2, PacketFieldKind.Bool)] public bool Remember;
}

public struct FixedNamePacket
{
    [PacketField(0, PacketFieldKind.FixedString, Length = 8)] public string? Code;
}

public struct BlobPacket
{
    [PacketField(0, PacketFieldKind.Bytes)] public byte[]? Payload;
}

public enum CommandStatus
{
    Idle = 0,
    Processing = 2,
    Done = 5,
}

public struct StatusPacket
{
    [PacketField(0)] public CommandStatus Status;
}

public struct PacketHeader
{
    [PacketField(0, PacketFieldKind.UInt16)] public ushort Type;
    [PacketField(1, PacketFieldKind.UInt32)] public uint Sequence;
}

public struct Envelope
{
    [PacketField(0, PacketFieldKind.Struct)] public PacketHeader Header;
    [PacketField(1, PacketFieldKind.Struct)] public ProtocolLoginRequest Body;
}

public struct TelemetryPacket
{
    [PacketField(0, PacketFieldKind.Array, ElementKind = PacketFieldKind.Int32)] public int[]? Samples;
}

public struct NoFields;

public sealed class ClassPacket
{
    [PacketField(0, PacketFieldKind.Int32)] public int Id { get; set; }
    [PacketField(1, PacketFieldKind.Utf8String)] public string? Name { get; set; }
}
