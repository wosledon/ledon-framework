using System.Runtime.CompilerServices;
using System.Text;

namespace Ledon.DataExchange;

/// <summary>RFC 4180 CSV 读写（支持引号、转义、字段内换行）。</summary>
public static class CsvFormat
{
    public static async IAsyncEnumerable<CsvRecord> ReadAsync(
        TextReader reader,
        DataExchangeOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        var parser = new Parser(reader);

        while (await parser.ReadRecordAsync(options, cancellationToken).ConfigureAwait(false) is { } record)
        {
            if (options.SkipEmptyLines
                && record.Fields.Count == 1
                && record.Fields[0].Length == 0
                && !record.Quoted)
            {
                continue;
            }

            yield return new CsvRecord(record.Fields, record.LineNumber);
        }
    }

    public static async Task WriteRowAsync(
        TextWriter writer,
        IReadOnlyList<string?> fields,
        DataExchangeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(fields);

        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                await writer.WriteAsync(options.Delimiter).ConfigureAwait(false);

            await writer.WriteAsync(Escape(fields[i], options)).ConfigureAwait(false);
        }

        await writer.WriteAsync(options.NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public static string Escape(string? value, DataExchangeOptions options)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var needsQuotes = value.Contains(options.Delimiter)
            || value.Contains(options.Quote)
            || value.Contains('\r')
            || value.Contains('\n');

        if (!needsQuotes)
            return value;

        return string.Concat(
            options.Quote,
            value.Replace(options.Quote.ToString(), new string(options.Quote, 2), StringComparison.Ordinal),
            options.Quote);
    }

    private sealed class Parser(TextReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _offset;
        private int _length;
        private int _line = 1;
        private bool _first = true;

        public async Task<ParsedRecord?> ReadRecordAsync(
            DataExchangeOptions options,
            CancellationToken cancellationToken)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var quoted = false;
            var anyContent = false;
            var fieldStart = true;
            var line = _line;

            while (true)
            {
                var next = await ReadCharAsync(cancellationToken).ConfigureAwait(false);

                if (next < 0)
                {
                    if (inQuotes)
                        throw new FormatException($"CSV 第 {line} 行引号未闭合");

                    if (!anyContent && fields.Count == 0)
                        return null;

                    fields.Add(field.ToString());
                    return new ParsedRecord(fields, line, quoted);
                }

                var ch = (char)next;
                anyContent = true;

                if (inQuotes)
                {
                    if (ch == options.Quote)
                    {
                        var following = await PeekAsync(cancellationToken).ConfigureAwait(false);

                        if (following == options.Quote)
                        {
                            _offset++;
                            field.Append(options.Quote);
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        if (ch == '\n')
                            _line++;

                        field.Append(ch);
                    }

                    continue;
                }

                if (ch == options.Quote && fieldStart && field.Length == 0)
                {
                    inQuotes = true;
                    quoted = true;
                    fieldStart = false;
                    continue;
                }

                if (ch == options.Delimiter)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                    fieldStart = true;
                    continue;
                }

                if (ch is '\r' or '\n')
                {
                    if (ch == '\r' && await PeekAsync(cancellationToken).ConfigureAwait(false) == '\n')
                        _offset++;

                    _line++;
                    fields.Add(field.ToString());

                    return new ParsedRecord(fields, line, quoted);
                }

                fieldStart = false;
                field.Append(ch);
            }
        }

        private async ValueTask<int> PeekAsync(CancellationToken cancellationToken)
        {
            if (_offset < _length)
                return _buffer[_offset];

            _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            _offset = 0;

            return _length == 0 ? -1 : _buffer[0];
        }

        private async ValueTask<int> ReadCharAsync(CancellationToken cancellationToken)
        {
            if (_offset >= _length)
            {
                _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;

                if (_length == 0)
                    return -1;
            }

            var ch = _buffer[_offset++];

            if (_first)
            {
                _first = false;

                if (ch == '\uFEFF')
                    return await ReadCharAsync(cancellationToken).ConfigureAwait(false);
            }

            return ch;
        }
    }

    private sealed record ParsedRecord(List<string> Fields, int LineNumber, bool Quoted);
}
