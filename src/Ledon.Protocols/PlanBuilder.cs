using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Ledon.Protocols;

/// <summary>
/// 布局计划构建器：为每个带 <see cref="PacketFieldAttribute"/> 的成员编译访问器。
/// 每种"成员类型 + 字段类型"组合一个访问器类（编译期确定，运行时无分支扩散）。
/// </summary>
[RequiresDynamicCode("协议布局在首次使用时编译访问器（表达式树）；裁剪/AOT 场景请改用显式读写。")]
internal static class PlanBuilder
{
    public static (PacketSerializer<T>.FieldAccessor<T>[] Fields, bool IsFixed, int FixedSize) Build<T>(Type type)
    {
        var members = type
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(member => member is FieldInfo or PropertyInfo)
            .Select(member => (Member: member, Attribute: member.GetCustomAttribute<PacketFieldAttribute>()))
            .Where(entry => entry.Attribute is not null)
            .OrderBy(entry => entry.Attribute!.Order)
            .ToArray();

        if (members.Length == 0)
            throw new ProtocolException($"类型 {type.Name} 没有任何 [PacketField] 成员");

        var accessors = new PacketSerializer<T>.FieldAccessor<T>[members.Length];
        var fixedSize = 0;
        var isFixed = true;

        for (var index = 0; index < members.Length; index++)
        {
            var (member, attribute) = members[index];
            var memberType = MemberType(member);
            var kind = ResolveKind(memberType, attribute!);

            accessors[index] = CreateAccessor<T>(member, memberType, kind, attribute!);

            if (IsFixedKind(kind, attribute!))
                fixedSize += FixedSizeOf(kind, memberType, attribute!);
            else
                isFixed = false;
        }

        return (accessors, isFixed, isFixed ? fixedSize : -1);
    }

    private static PacketSerializer<T>.FieldAccessor<T> CreateAccessor<T>(
        MemberInfo member,
        Type memberType,
        PacketFieldKind kind,
        PacketFieldAttribute attribute)
    {
        var accessorType = kind switch
        {
            PacketFieldKind.Struct => typeof(StructAccessor<,>).MakeGenericType(typeof(T), memberType),
            PacketFieldKind.Array => typeof(ArrayAccessor<,>).MakeGenericType(typeof(T), ElementType(memberType)),
            _ => typeof(PrimitiveAccessor<,>).MakeGenericType(typeof(T), memberType),
        };

        var accessor = Activator.CreateInstance(accessorType)
            ?? throw new ProtocolException($"无法创建访问器：{accessorType.Name}");

        // 编译强类型访问器（零反射、零装箱）
        var setter = BuildSetter<T>(member, memberType);
        var getter = BuildGetter<T>(member, memberType);

        accessorType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(accessor, [setter, getter, kind, attribute]);

        return (PacketSerializer<T>.FieldAccessor<T>)accessor;
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => throw new ProtocolException($"不支持的成员类型：{member.GetType().Name}"),
    };

    private static Type ElementType(Type arrayType) =>
        arrayType.IsArray ? arrayType.GetElementType()! : throw new ProtocolException($"数组字段类型应为 T[]：{arrayType.Name}");

    private static PacketFieldKind ResolveKind(Type memberType, PacketFieldAttribute attribute)
    {
        if (attribute.Kind != PacketFieldKind.Auto)
            return attribute.Kind;

        var underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (underlying.IsEnum)
            return ResolveKind(Enum.GetUnderlyingType(underlying), new PacketFieldAttribute(attribute.Order));

        if (underlying == typeof(bool)) return PacketFieldKind.Bool;
        if (underlying == typeof(sbyte)) return PacketFieldKind.Int8;
        if (underlying == typeof(byte)) return PacketFieldKind.UInt8;
        if (underlying == typeof(short)) return PacketFieldKind.Int16;
        if (underlying == typeof(ushort)) return PacketFieldKind.UInt16;
        if (underlying == typeof(int)) return PacketFieldKind.Int32;
        if (underlying == typeof(uint)) return PacketFieldKind.UInt32;
        if (underlying == typeof(long)) return PacketFieldKind.Int64;
        if (underlying == typeof(ulong)) return PacketFieldKind.UInt64;
        if (underlying == typeof(float)) return PacketFieldKind.Float32;
        if (underlying == typeof(double)) return PacketFieldKind.Double;
        if (underlying == typeof(string)) return PacketFieldKind.Utf8String;
        if (underlying == typeof(byte[])) return PacketFieldKind.Bytes;
        if (underlying.IsArray) return PacketFieldKind.Array;
        if (underlying.IsClass || underlying.IsValueType) return PacketFieldKind.Struct;

        throw new ProtocolException($"无法推断字段类型：{memberType.Name}（请显式指定 PacketFieldAttribute.Kind）");
    }

    private static bool IsFixedKind(PacketFieldKind kind, PacketFieldAttribute attribute) => kind switch
    {
        PacketFieldKind.Bool or PacketFieldKind.Int8 or PacketFieldKind.UInt8 => true,
        PacketFieldKind.Int16 or PacketFieldKind.UInt16 => true,
        PacketFieldKind.Int32 or PacketFieldKind.UInt32 or PacketFieldKind.Float32 => true,
        PacketFieldKind.Int64 or PacketFieldKind.UInt64 or PacketFieldKind.Double => true,
        PacketFieldKind.FixedString or PacketFieldKind.FixedBytes => attribute.Length > 0,
        _ => false,
    };

    private static int FixedSizeOf(PacketFieldKind kind, Type memberType, PacketFieldAttribute attribute) => kind switch
    {
        PacketFieldKind.Bool or PacketFieldKind.Int8 or PacketFieldKind.UInt8 => 1,
        PacketFieldKind.Int16 or PacketFieldKind.UInt16 => 2,
        PacketFieldKind.Int32 or PacketFieldKind.UInt32 or PacketFieldKind.Float32 => 4,
        PacketFieldKind.Int64 or PacketFieldKind.UInt64 or PacketFieldKind.Double => 8,
        PacketFieldKind.FixedString or PacketFieldKind.FixedBytes => attribute.Length,
        _ => throw new ProtocolException($"类型 {kind} 不是定长字段"),
    };

    private static Delegate BuildSetter<T>(MemberInfo member, Type memberType)
    {
        var instance = Expression.Parameter(typeof(T).MakeByRefType(), "instance");
        var value = Expression.Parameter(memberType, "value");
        var body = Expression.Assign(MemberAccess(member, instance), value);
        var delegateType = typeof(RefSetter<,>).MakeGenericType(typeof(T), memberType);

        return Expression.Lambda(delegateType, body, instance, value).Compile();
    }

    private static Delegate BuildGetter<T>(MemberInfo member, Type memberType)
    {
        var instance = Expression.Parameter(typeof(T), "instance");
        var body = MemberAccess(member, instance);
        var delegateType = typeof(RefGetter<,>).MakeGenericType(typeof(T), memberType);

        return Expression.Lambda(delegateType, body, instance).Compile();
    }

    private static Expression MemberAccess(MemberInfo member, Expression instance) => member switch
    {
        FieldInfo field => Expression.Field(instance, field),
        PropertyInfo property => Expression.Property(instance, property),
        _ => throw new ProtocolException($"不支持的成员类型：{member.GetType().Name}"),
    };
}
