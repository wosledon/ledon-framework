using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ledon.OpenIddict;

/// <summary>
/// 内存存储的属性袋：OpenIddict 的 Get/SetAsync``2 以属性表达式寻址，
/// 这里按表达式解析出的属性名把值存在实例旁的弱引用袋里（实例即 POCO，不引入基类）。
/// </summary>
internal static class StoreBag
{
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, object?>> Bags = new();

    public static ConcurrentDictionary<string, object?> Get(object instance) =>
        Bags.GetValue(instance, _ => new ConcurrentDictionary<string, object?>(StringComparer.Ordinal));

    public static string Name<T, TProperty>(Expression<Func<T, TProperty>> property) =>
        property.Body is MemberExpression { Member: System.Reflection.PropertyInfo propertyInfo }
            ? propertyInfo.Name
            : throw new InvalidOperationException("属性表达式必须是成员访问。");

    /// <summary>OpenIddict 期望集合属性返回空集合而不是 null（ImmutableArray 默认值会直接 NRE）。</summary>
    public static T Empty<T>()
    {
        if (typeof(T) == typeof(ImmutableArray<string>))
            return (T)(object)ImmutableArray<string>.Empty;

        if (typeof(T) == typeof(ImmutableDictionary<string, string>))
            return (T)(object)ImmutableDictionary<string, string>.Empty;

        if (typeof(T) == typeof(ImmutableDictionary<string, JsonElement>))
            return (T)(object)ImmutableDictionary<string, JsonElement>.Empty;

        if (typeof(T) == typeof(ImmutableDictionary<CultureInfo, string>))
            return (T)(object)ImmutableDictionary<CultureInfo, string>.Empty;

        return default!;
    }
}
