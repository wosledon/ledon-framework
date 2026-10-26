using System.Globalization;
using System.Reflection;

namespace Ledon.DataExchange;

/// <summary>列名（导出/导入表头）。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DataColumnAttribute(string name, int order = 0) : Attribute
{
    public string Name { get; } = name;

    public int Order { get; } = order;
}

/// <summary>忽略该属性。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DataIgnoreAttribute : Attribute;

/// <summary>通用数据交换选项。</summary>
public sealed class DataExchangeOptions
{
    /// <summary>CSV 分隔符，默认逗号。</summary>
    public char Delimiter { get; set; } = ',';

    /// <summary>CSV 引号，默认双引号。</summary>
    public char Quote { get; set; } = '"';

    /// <summary>CSV 换行符，默认 CRLF（Excel 兼容）。</summary>
    public string NewLine { get; set; } = "\r\n";

    /// <summary>是否包含表头，默认 <c>true</c>。</summary>
    public bool IncludeHeader { get; set; } = true;

    /// <summary>CSV 编码，默认 UTF-8（无 BOM）。</summary>
    public System.Text.Encoding Encoding { get; set; } = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Excel 工作表名，默认 Sheet1。</summary>
    public string SheetName { get; set; } = "Sheet1";

    /// <summary>导入时最大错误数（防止坏文件耗尽内存），默认 100。</summary>
    public int MaxErrors { get; set; } = 100;

    /// <summary>跳过空行，默认 <c>true</c>。</summary>
    public bool SkipEmptyLines { get; set; } = true;

    /// <summary>转换文化，默认不变文化。</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
}

/// <summary>导入错误。</summary>
public sealed record ImportError(int RowNumber, string? Column, string Message);

/// <summary>导入结果。</summary>
public sealed record ImportResult<T>(IReadOnlyList<T> Rows, IReadOnlyList<ImportError> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>CSV/Excel 流式导入导出。</summary>
public interface IDataExchange
{
    Task ExportCsvAsync<T>(Stream stream, IEnumerable<T> rows, CancellationToken cancellationToken = default);

    Task<ImportResult<T>> ImportCsvAsync<T>(Stream stream, CancellationToken cancellationToken = default)
        where T : new();

    Task ExportExcelAsync<T>(Stream stream, IEnumerable<T> rows, CancellationToken cancellationToken = default);

    Task<ImportResult<T>> ImportExcelAsync<T>(Stream stream, CancellationToken cancellationToken = default)
        where T : new();
}

/// <summary>列元数据缓存。</summary>
internal static class DataColumnCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Column[]> Cache = new();

    public static Column[] Get<T>() => Cache.GetOrAdd(typeof(T), static type =>
    {
        var columns = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, CanWrite: true }
                && property.GetIndexParameters().Length == 0
                && !property.IsDefined(typeof(DataIgnoreAttribute), inherit: true))
            .Select(property =>
            {
                var attribute = property.GetCustomAttribute<DataColumnAttribute>(inherit: true);
                return new Column(
                    property,
                    attribute?.Name ?? property.Name,
                    attribute?.Order ?? 0,
                    Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
                    Nullable.GetUnderlyingType(property.PropertyType) is not null || !property.PropertyType.IsValueType);
            })
            .OrderBy(column => column.Order)
            .ThenBy(column => Array.IndexOf(type.GetProperties(), column.Property))
            .ToArray();

        if (columns.Length == 0)
            throw new InvalidOperationException($"类型 {type.Name} 没有可导出的公共属性");

        return columns;
    });
}

internal sealed record Column(PropertyInfo Property, string Header, int Order, Type Type, bool Nullable);

/// <summary>CSV 行。</summary>
public sealed record CsvRecord(IReadOnlyList<string> Fields, int LineNumber);
