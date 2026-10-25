using System.Buffers;
using System.Linq.Expressions;
using System.Reflection;

namespace Ledon.Protocols;

/// <summary>按引用写入成员的委托（结构体也能原地改）。</summary>
/// <typeparam name="T">宿主类型。</typeparam>
/// <typeparam name="TMember">成员类型。</typeparam>
public delegate void RefSetter<T, in TMember>(ref T instance, TMember value);

/// <summary>读取成员的委托。</summary>
public delegate TMember RefGetter<T, out TMember>(T instance);

/// <summary>
/// 协议解析器：把带 <see cref="PacketFieldAttribute"/> 的类/结构体与二进制布局互转。
/// <para>
/// 设计要点（性能）：
/// </para>
/// <list type="bullet">
///   <item>首次使用时为每个成员编译强类型访问器（表达式树），之后**零反射、零装箱**；</item>
///   <item>读写在 <see cref="PacketReader"/> / <see cref="PacketWriter"/>（ref struct）上进行，无中间数组；</item>
///   <item>布局即契约：两端各写一份结构体即可通信，不需要 IDL / schema 文件；</item>
///   <item>嵌套结构体与数组递归应用同一套规则。</item>
/// </list>
/// <code>
/// public struct LoginRequest
/// {
///     [PacketField(0, PacketFieldKind.UInt32)] public uint Version;
///     [PacketField(1, PacketFieldKind.Utf8String)] public string User;
///     [PacketField(2, PacketFieldKind.Bool)] public bool Remember;
/// }
///
/// var bytes = PacketSerializer&lt;LoginRequest&gt;.Serialize(new LoginRequest { ... });
/// var request = PacketSerializer&lt;LoginRequest&gt;.Deserialize(bytes);
/// </code>
/// </summary>
public static class PacketSerializer<T>
{
    private static readonly (FieldAccessor<T>[] Fields, bool IsFixed, int Size) Plan = PlanBuilder.Build<T>(typeof(T));

    internal static FieldAccessor<T>[] Fields => Plan.Fields;
    internal static bool IsFixedLayout => Plan.IsFixed;
    internal static int FixedSize => Plan.Size;

    /// <summary>是否为定长布局（全部字段定长时可预计算大小）。</summary>
    public static bool IsFixedSize => IsFixedLayout;

    /// <summary>定长布局的字节数；变长布局为 -1。</summary>
    public static int Size => FixedSize;

    /// <summary>字段数。</summary>
    public static int FieldCount => Fields.Length;

    /// <summary>从缓冲区解析（剩余字节忽略：上层帧编解码已切好边界）。</summary>
    public static T Read(ReadOnlySpan<byte> buffer, bool bigEndian = false)
    {
        var reader = new PacketReader(buffer, bigEndian);
        var value = CreateInstance();

        foreach (var field in Fields)
            field.Read(ref reader, ref value);

        return value;
    }

    /// <summary>写入缓冲区。</summary>
    public static void Write(T value, IBufferWriter<byte> writer, bool bigEndian = false)
    {
        var packetWriter = new PacketWriter(writer, bigEndian);
        Write(value, ref packetWriter);
    }

    /// <summary>写入已有写入器（嵌套结构体复用同一个缓冲区，不产生中间数组）。</summary>
    public static void Write(T value, ref PacketWriter writer)
    {
        foreach (var field in Fields)
            field.Write(ref writer, ref value);
    }

    /// <summary>序列化为字节数组（便捷重载）。</summary>
    public static byte[] Serialize(T value, bool bigEndian = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(value, buffer, bigEndian);

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary><see cref="Read"/> 的别名（语义更清晰）。</summary>
    public static T Deserialize(ReadOnlySpan<byte> buffer, bool bigEndian = false) => Read(buffer, bigEndian);

    /// <summary>计算序列化后的字节数（变长布局按实际内容算）。</summary>
    public static int Measure(T value, bool bigEndian = false)
    {
        var buffer = new SizeOnlyBufferWriter();
        Write(value, buffer, bigEndian);

        return buffer.WrittenCount;
    }

    private static T CreateInstance()
    {
        var value = default(T);

        return value is null ? Activator.CreateInstance<T>() : value;
    }

    /// <summary>字段访问器：一个字段一次虚调用完成读+赋值（无反射、无装箱）。</summary>
    public abstract class FieldAccessor<TInstance>
    {
        public abstract void Read(ref PacketReader reader, ref TInstance target);

        public abstract void Write(ref PacketWriter writer, ref TInstance source);
    }

    /// <summary>只算大小的 IBufferWriter（Measure 用，不分配）。</summary>
    /// <summary>只算大小的 IBufferWriter（Measure 用，不分配）。</summary>
    private sealed class SizeOnlyBufferWriter : IBufferWriter<byte>
    {
        private byte[] _scratch = new byte[64];

        public int WrittenCount { get; private set; }

        public void Advance(int count) => WrittenCount += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Ensure(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Ensure(sizeHint).Span;

        private Memory<byte> Ensure(int sizeHint)
        {
            var needed = WrittenCount + Math.Max(sizeHint, 16);
            if (_scratch.Length < needed)
                Array.Resize(ref _scratch, needed * 2);

            return new Memory<byte>(_scratch, WrittenCount, _scratch.Length - WrittenCount);
        }
    }
}
