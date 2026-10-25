namespace Ledon.Protocols;

/// <summary>字段类型（二进制布局）。未指定时按成员 CLR 类型推断。</summary>
public enum PacketFieldKind
{
    /// <summary>按 CLR 类型推断。</summary>
    Auto = 0,

    /// <summary>1 字节布尔。</summary>
    Bool,

    /// <summary>1 字节有符号整数。</summary>
    Int8,

    /// <summary>1 字节无符号整数。</summary>
    UInt8,

    /// <summary>2 字节有符号整数。</summary>
    Int16,

    /// <summary>2 字节无符号整数。</summary>
    UInt16,

    /// <summary>4 字节有符号整数。</summary>
    Int32,

    /// <summary>4 字节无符号整数。</summary>
    UInt32,

    /// <summary>8 字节有符号整数。</summary>
    Int64,

    /// <summary>8 字节无符号整数。</summary>
    UInt64,

    /// <summary>4 字节单精度浮点。</summary>
    Float32,

    /// <summary>8 字节双精度浮点。</summary>
    Double,

    /// <summary>2 字节长度前缀 + UTF-8 字符串。</summary>
    Utf8String,

    /// <summary>定长字节的字符串（不足补 0，超出截断）。</summary>
    FixedString,

    /// <summary>2 字节长度前缀 + 原始字节。</summary>
    Bytes,

    /// <summary>定长原始字节。</summary>
    FixedBytes,

    /// <summary>嵌套结构体（递归应用同一套布局规则）。</summary>
    Struct,

    /// <summary>2 字节元素个数 + 元素数组。</summary>
    Array,
}

/// <summary>字符串编码。</summary>
public enum PacketStringEncoding
{
    /// <summary>UTF-8。</summary>
    Utf8 = 0,

    /// <summary>ASCII。</summary>
    Ascii,

    /// <summary>UTF-16。</summary>
    Utf16,
}

/// <summary>
/// 标记一个成员参与二进制布局。按 <see cref="Order"/> 升序读写——
/// 布局即契约，两端各写一份结构体就能通信（无需 IDL / schema 文件）。
/// <code>
/// public struct LoginRequest
/// {
///     [PacketField(0, PacketFieldKind.UInt32)] public uint Version;
///     [PacketField(1, PacketFieldKind.Utf8String)] public string User;
///     [PacketField(2, PacketFieldKind.Bool)] public bool Remember;
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class PacketFieldAttribute(int order, PacketFieldKind kind = PacketFieldKind.Auto) : Attribute
{
    /// <summary>读写顺序（小的在前）。</summary>
    public int Order { get; } = order;

    /// <summary>字段类型；<see cref="PacketFieldKind.Auto"/> 时按 CLR 类型推断。</summary>
    public PacketFieldKind Kind { get; set; } = kind;

    /// <summary>定长字段的字节数（<see cref="PacketFieldKind.FixedString"/> / <see cref="PacketFieldKind.FixedBytes"/>）。</summary>
    public int Length { get; set; }

    /// <summary>数组元素类型（<see cref="PacketFieldKind.Array"/> 时按 CLR 类型推断）。</summary>
    public PacketFieldKind ElementKind { get; set; } = PacketFieldKind.Auto;

    /// <summary>字符串编码。</summary>
    public PacketStringEncoding Encoding { get; set; } = PacketStringEncoding.Utf8;

    /// <summary>该字段用大端（覆盖全局 <see cref="PacketLayoutOptions.BigEndian"/>）。</summary>
    public bool BigEndian { get; set; }
}

/// <summary>布局选项。</summary>
public sealed class PacketLayoutOptions
{
    /// <summary>默认大端（网络字节序）；false = 小端。</summary>
    public bool BigEndian { get; set; }
}
