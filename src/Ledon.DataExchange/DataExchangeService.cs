using System.ComponentModel;
using System.Globalization;

namespace Ledon.DataExchange;

/// <summary>CSV/Excel 通用导入导出实现。</summary>
public sealed partial class DataExchangeService(DataExchangeOptions options) : IDataExchange
{
    public async Task ExportCsvAsync<T>(
        Stream stream,
        IEnumerable<T> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rows);

        await using var writer = new StreamWriter(stream, options.Encoding, leaveOpen: true);

        var columns = DataColumnCache.Get<T>();

        if (options.IncludeHeader)
            await CsvFormat.WriteRowAsync(writer, columns.Select(column => (string?)column.Header).ToArray(), options, cancellationToken)
                .ConfigureAwait(false);

        foreach (var row in rows)
        {
            var values = columns
                .Select(column => FormatValue(column.Property.GetValue(row), options.Culture))
                .ToArray();

            await CsvFormat.WriteRowAsync(writer, values, options, cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);    }

    public async Task<ImportResult<T>> ImportCsvAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default) where T : new()
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream,
            options.Encoding,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var records = new List<CsvRecord>();

        await foreach (var record in CsvFormat.ReadAsync(reader, options, cancellationToken).ConfigureAwait(false))
            records.Add(record);

        return ImportCore<T>(records);
    }

    internal ImportResult<T> ImportCore<T>(IEnumerable<CsvRecord> records) where T : new()
    {
        var rows = new List<T>();
        var errors = new List<ImportError>();
        var columns = DataColumnCache.Get<T>();

        Dictionary<Column, int>? mapping = null;

        foreach (var record in records)
        {
            if (mapping is null && options.IncludeHeader)
            {
                mapping = BuildMapping(record.Fields, columns);
                continue;
            }

            mapping ??= columns
                .Select((column, index) => (column, index))
                .ToDictionary(pair => pair.column, pair => pair.index);

            if (record.Fields.All(string.IsNullOrWhiteSpace))
                continue;

            var item = new T();
            var valid = true;

            foreach (var column in columns)
            {
                if (!mapping.TryGetValue(column, out var index) || index >= record.Fields.Count)
                    continue;

                var raw = record.Fields[index];

                try
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        if (column.Nullable)
                            column.Property.SetValue(item, null);

                        continue;
                    }

                    column.Property.SetValue(item, ConvertValue(raw, column.Type, options.Culture));
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
                {
                    if (errors.Count < options.MaxErrors)
                        errors.Add(new ImportError(record.LineNumber, column.Header, ex.Message));

                    valid = false;
                    break;
                }
            }

            if (valid)
                rows.Add(item);
        }

        return new ImportResult<T>(rows, errors);
    }

    internal static Dictionary<Column, int> BuildMapping(IReadOnlyList<string> headers, Column[] columns)
    {
        var mapping = new Dictionary<Column, int>();

        foreach (var column in columns)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (string.Equals(headers[i], column.Header, StringComparison.OrdinalIgnoreCase))
                {
                    mapping[column] = i;
                    break;
                }
            }
        }

        return mapping;
    }

    internal static string? FormatValue(object? value, CultureInfo culture) => value switch
    {
        null => null,
        string text => text,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D"),
        bool boolean => boolean ? "true" : "false",
        Enum @enum => @enum.ToString(),
        IFormattable formattable => formattable.ToString(null, culture),
        _ => value.ToString(),
    };

    internal static object ConvertValue(string value, Type type, CultureInfo culture)
    {
        if (type == typeof(string))
            return value;

        if (type == typeof(Guid))
            return Guid.Parse(value);

        if (type == typeof(bool))
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" => true,
                "false" or "0" or "no" or "n" => false,
                _ => throw new FormatException($"无法转换为布尔值：{value}"),
            };
        }

        if (type == typeof(DateTime))
            return DateTime.Parse(value, culture, DateTimeStyles.RoundtripKind);

        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(value, culture, DateTimeStyles.RoundtripKind);

        if (type == typeof(DateOnly))
            return DateOnly.Parse(value, culture);

        if (type == typeof(TimeOnly))
            return TimeOnly.Parse(value, culture);

        if (type.IsEnum)
            return Enum.Parse(type, value, ignoreCase: true);

        if (type == typeof(byte[]))
            return Convert.FromBase64String(value);

        var converter = TypeDescriptor.GetConverter(type);

        if (converter.CanConvertFrom(typeof(string)))
            return converter.ConvertFromString(null, culture, value)!;

        return Convert.ChangeType(value, type, culture);
    }
}
