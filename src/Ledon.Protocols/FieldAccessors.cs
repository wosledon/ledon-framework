using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace Ledon.Protocols;

/// <summary>基础访问器：保存编译好的强类型读写委托。</summary>
public abstract class FieldAccessorBase<T, TMember> : PacketSerializer<T>.FieldAccessor<T>
{
    private RefSetter<T, TMember>? _setter;
    private RefGetter<T, TMember>? _getter;
    private PacketFieldKind _kind;
    private PacketFieldAttribute? _attribute;

    /// <summary>字段类型。</summary>
    protected PacketFieldKind Kind => _kind;

    /// <summary>字段特性。</summary>
    protected PacketFieldAttribute Attribute => _attribute!;

    /// <summary>是否用大端：字段级 <see cref="PacketFieldAttribute.BigEndian"/> 或全局端序。</summary>
    protected bool IsBigEndian(in PacketReader reader) => _attribute!.BigEndian || reader.BigEndian;

    protected bool IsBigEndian(in PacketWriter writer) => _attribute!.BigEndian || writer.BigEndian;

    /// <summary>由计划构建器注入编译结果。</summary>
    public void Initialize(Delegate setter, Delegate getter, PacketFieldKind kind, PacketFieldAttribute attribute)
    {
        _setter = (RefSetter<T, TMember>)setter;
        _getter = (RefGetter<T, TMember>)getter;
        _kind = kind;
        _attribute = attribute;
    }

    /// <inheritdoc />
    public override void Read(ref PacketReader reader, ref T target)
    {
        var value = ReadValue(ref reader);
        _setter!(ref target, value);
    }

    /// <inheritdoc />
    public override void Write(ref PacketWriter writer, ref T source)
    {
        WriteValue(ref writer, _getter!(source));
    }

    /// <summary>读取一个成员值。</summary>
    protected abstract TMember ReadValue(ref PacketReader reader);

    /// <summary>写入一个成员值。</summary>
    protected abstract void WriteValue(ref PacketWriter writer, TMember value);
}

/// <summary>基础类型字段访问器（数值/布尔/字符串/字节）。</summary>
public sealed class PrimitiveAccessor<T, TMember> : FieldAccessorBase<T, TMember>
{
    protected override TMember ReadValue(ref PacketReader reader)
    {
        var bigEndian = IsBigEndian(reader);

        // 数值类型：按字段类型逐个分发（TMember 已由计划固定，分支可预测）
        if (typeof(TMember) == typeof(int)) return Cast(reader.ReadInt32(bigEndian));
        if (typeof(TMember) == typeof(uint)) return Cast(reader.ReadUInt32(bigEndian));
        if (typeof(TMember) == typeof(short)) return Cast(reader.ReadInt16(bigEndian));
        if (typeof(TMember) == typeof(ushort)) return Cast(reader.ReadUInt16(bigEndian));
        if (typeof(TMember) == typeof(long)) return Cast(reader.ReadInt64(bigEndian));
        if (typeof(TMember) == typeof(ulong)) return Cast(reader.ReadUInt64(bigEndian));
        if (typeof(TMember) == typeof(byte)) return Cast(reader.ReadByte());
        if (typeof(TMember) == typeof(sbyte)) return Cast(reader.ReadSByte());
        if (typeof(TMember) == typeof(bool)) return Cast(reader.ReadBool());
        if (typeof(TMember) == typeof(float)) return Cast(reader.ReadFloat32(bigEndian));
        if (typeof(TMember) == typeof(double)) return Cast(reader.ReadDouble(bigEndian));

        // 枚举：按底层类型读后转枚举
        if (typeof(TMember).IsEnum)
            return (TMember)(object)Enum.ToObject(typeof(TMember), ReadUnderlying(ref reader, typeof(TMember), IsBigEndian(reader)));

        if (typeof(TMember) == typeof(string))
            return (TMember)(object)ReadString(ref reader);

        if (typeof(TMember) == typeof(byte[]))
            return (TMember)(object)ReadBytes(ref reader);

        throw new ProtocolException($"不支持的字段类型：{typeof(TMember).Name}");
    }

    protected override void WriteValue(ref PacketWriter writer, TMember value)
    {
        var bigEndian = IsBigEndian(writer);

        if (typeof(TMember) == typeof(int)) writer.WriteInt32(Uncast<int>(value), bigEndian);
        else if (typeof(TMember) == typeof(uint)) writer.WriteUInt32(Uncast<uint>(value), bigEndian);
        else if (typeof(TMember) == typeof(short)) writer.WriteInt16(Uncast<short>(value), bigEndian);
        else if (typeof(TMember) == typeof(ushort)) writer.WriteUInt16(Uncast<ushort>(value), bigEndian);
        else if (typeof(TMember) == typeof(long)) writer.WriteInt64(Uncast<long>(value), bigEndian);
        else if (typeof(TMember) == typeof(ulong)) writer.WriteUInt64(Uncast<ulong>(value), bigEndian);
        else if (typeof(TMember) == typeof(byte)) writer.WriteByte(Uncast<byte>(value));
        else if (typeof(TMember) == typeof(sbyte)) writer.WriteSByte(Uncast<sbyte>(value));
        else if (typeof(TMember) == typeof(bool)) writer.WriteBool(Uncast<bool>(value));
        else if (typeof(TMember) == typeof(float)) writer.WriteFloat32(Uncast<float>(value));
        else if (typeof(TMember) == typeof(double)) writer.WriteDouble(Uncast<double>(value));
        else if (typeof(TMember).IsEnum) WriteUnderlying(ref writer, value, IsBigEndian(writer));
        else if (typeof(TMember) == typeof(string)) WriteString(ref writer, (string?)(object?)value);
        else if (typeof(TMember) == typeof(byte[])) writer.WriteBytes((byte[])(object)value!);
        else throw new ProtocolException($"不支持的字段类型：{typeof(TMember).Name}");
    }

    private string ReadString(ref PacketReader reader) => Kind switch
    {
        PacketFieldKind.FixedString => reader.ReadFixedString(Attribute.Length, Attribute.Encoding),
        _ => reader.ReadString(Attribute.Encoding),
    };


    private byte[] ReadBytes(ref PacketReader reader) => Kind switch
    {
        PacketFieldKind.FixedBytes => reader.ReadBytes(Attribute.Length),
        _ => reader.ReadBytes(),
    };

    private void WriteString(ref PacketWriter writer, string? value)
    {
        if (Kind == PacketFieldKind.FixedString)
            writer.WriteFixedString(value, Attribute.Length, Attribute.Encoding);
        else
            writer.WriteString(value, Attribute.Encoding);
    }

    private object ReadUnderlying(ref PacketReader reader, Type enumType, bool bigEndian)
    {
        var underlying = Enum.GetUnderlyingType(enumType);

        if (underlying == typeof(int)) return reader.ReadInt32(IsBigEndian(reader));
        if (underlying == typeof(int)) return reader.ReadInt32(bigEndian);
        if (underlying == typeof(uint)) return reader.ReadUInt32(bigEndian);
        if (underlying == typeof(short)) return reader.ReadInt16(bigEndian);
        if (underlying == typeof(ushort)) return reader.ReadUInt16(bigEndian);
        if (underlying == typeof(long)) return reader.ReadInt64(bigEndian);
        if (underlying == typeof(ulong)) return reader.ReadUInt64(bigEndian);
        if (underlying == typeof(sbyte)) return reader.ReadSByte();

        throw new ProtocolException($"不支持的枚举底层类型：{underlying.Name}");
    }

    private void WriteUnderlying(ref PacketWriter writer, TMember value, bool bigEndian)
    {
        var underlying = Enum.GetUnderlyingType(typeof(TMember));
        var raw = Convert.ChangeType(value, underlying);

        if (underlying == typeof(int)) writer.WriteInt32((int)raw!, bigEndian);
        else if (underlying == typeof(uint)) writer.WriteUInt32((uint)raw!, bigEndian);
        else if (underlying == typeof(short)) writer.WriteInt16((short)raw!, bigEndian);
        else if (underlying == typeof(ushort)) writer.WriteUInt16((ushort)raw!, bigEndian);
        else if (underlying == typeof(long)) writer.WriteInt64((long)raw!, bigEndian);
        else if (underlying == typeof(ulong)) writer.WriteUInt64((ulong)raw!, bigEndian);
        else if (underlying == typeof(byte)) writer.WriteByte((byte)raw!);
        else if (underlying == typeof(sbyte)) writer.WriteSByte((sbyte)raw!);
    }

    private static TMember Cast<TValue>(TValue value) where TValue : struct =>
        System.Runtime.CompilerServices.Unsafe.As<TValue, TMember>(ref value);

    private static TValue Uncast<TValue>(TMember value) where TValue : struct =>
        System.Runtime.CompilerServices.Unsafe.As<TMember, TValue>(ref value);
}

/// <summary>嵌套结构体字段访问器（递归调用该类型的解析器）。</summary>
public sealed class StructAccessor<T, TMember> : FieldAccessorBase<T, TMember>
{
    protected override TMember ReadValue(ref PacketReader reader)
    {
        var value = PacketSerializer<TMember>.Read(
            reader.Buffer.Slice(reader.Position, reader.Remaining),
            IsBigEndian(reader));

        // 嵌套结构体读完后按自身布局推进位置（Measure 不分配）
        reader.Skip(PacketSerializer<TMember>.Measure(value, IsBigEndian(reader)));

        return value;
    }

    protected override void WriteValue(ref PacketWriter writer, TMember value) =>
        PacketSerializer<TMember>.Write(value, ref writer);
}

/// <summary>数组字段访问器：2 字节元素个数 + 逐个元素。</summary>
public sealed class ArrayAccessor<T, TElement> : FieldAccessorBase<T, TElement[]>
{
    protected override TElement[] ReadValue(ref PacketReader reader)
    {
        var count = reader.ReadUInt16(IsBigEndian(reader));
        if (count == 0)
            return [];

        var result = new TElement[count];

        for (var index = 0; index < count; index++)
        {
            if (typeof(TElement) == typeof(int)) result[index] = As<int>(reader.ReadInt32(IsBigEndian(reader)));
            else if (typeof(TElement) == typeof(uint)) result[index] = As<uint>(reader.ReadUInt32(IsBigEndian(reader)));
            else if (typeof(TElement) == typeof(short)) result[index] = As<short>(reader.ReadInt16(IsBigEndian(reader)));
            else if (typeof(TElement) == typeof(ushort)) result[index] = As<ushort>(reader.ReadUInt16(IsBigEndian(reader)));
            else if (typeof(TElement) == typeof(long)) result[index] = As<long>(reader.ReadInt64(IsBigEndian(reader)));
            else if (typeof(TElement) == typeof(ulong)) result[index] = As<ulong>(reader.ReadUInt64(IsBigEndian(reader)));
            else if (typeof(TElement) == typeof(byte)) result[index] = As<byte>(reader.ReadByte());
            else if (typeof(TElement) == typeof(bool)) result[index] = As<bool>(reader.ReadBool());
            else if (typeof(TElement) == typeof(string)) result[index] = (TElement)(object)reader.ReadString(Attribute.Encoding);
            else if (typeof(TElement) == typeof(byte[])) result[index] = (TElement)(object)reader.ReadBytes();
            else if (typeof(TElement).IsClass || typeof(TElement).IsValueType)
            {
                result[index] = PacketSerializer<TElement>.Read(reader.Buffer.Slice(reader.Position, reader.Remaining), IsBigEndian(reader));

                // 嵌套结构体读完后推进位置：按其自身布局长度
                reader.Skip(PacketSerializer<TElement>.Measure(result[index], IsBigEndian(reader)));
            }
            else throw new ProtocolException($"不支持的数组元素类型：{typeof(TElement).Name}");
        }

        return result;
    }

    protected override void WriteValue(ref PacketWriter writer, TElement[] value)
    {
        var items = value ?? [];
        if (items.Length > ushort.MaxValue)
            throw new ProtocolException($"数组过长（{items.Length} > {ushort.MaxValue}）");

        writer.WriteUInt16((ushort)items.Length);

        foreach (var item in items)
        {
            if (typeof(TElement) == typeof(int)) writer.WriteInt32(Un<int>(item), IsBigEndian(writer));
            else if (typeof(TElement) == typeof(uint)) writer.WriteUInt32(Un<uint>(item), IsBigEndian(writer));
            else if (typeof(TElement) == typeof(short)) writer.WriteInt16(Un<short>(item), IsBigEndian(writer));
            else if (typeof(TElement) == typeof(ushort)) writer.WriteUInt16(Un<ushort>(item), IsBigEndian(writer));
            else if (typeof(TElement) == typeof(long)) writer.WriteInt64(Un<long>(item), IsBigEndian(writer));
            else if (typeof(TElement) == typeof(ulong)) writer.WriteUInt64(Un<ulong>(item), IsBigEndian(writer));
            else if (typeof(TElement) == typeof(byte)) writer.WriteByte(Un<byte>(item));
            else if (typeof(TElement) == typeof(bool)) writer.WriteBool(Un<bool>(item));
            else if (typeof(TElement) == typeof(string)) writer.WriteString((string?)(object?)item, Attribute.Encoding);
            else if (typeof(TElement) == typeof(byte[])) writer.WriteBytes((byte[])(object)item!);
            else if (typeof(TElement).IsClass || typeof(TElement).IsValueType)
                PacketSerializer<TElement>.Write(item, ref writer);
            else throw new ProtocolException($"不支持的数组元素类型：{typeof(TElement).Name}");
        }
    }

    private static TElement As<TValue>(TValue value) where TValue : struct =>
        System.Runtime.CompilerServices.Unsafe.As<TValue, TElement>(ref value);

    private static TValue Un<TValue>(TElement value) where TValue : struct =>
        System.Runtime.CompilerServices.Unsafe.As<TElement, TValue>(ref value);
}
