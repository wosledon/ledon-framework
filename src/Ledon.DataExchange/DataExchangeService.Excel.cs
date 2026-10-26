using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Ledon.DataExchange;

public sealed partial class DataExchangeService
{
    public async Task ExportExcelAsync<T>(
        Stream stream,
        IEnumerable<T> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rows);

        var columns = DataColumnCache.Get<T>();

        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();

        using (var writer = OpenXmlWriter.Create(worksheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());

            var rowIndex = 1U;

            if (options.IncludeHeader)
                WriteRow(writer, columns.Select(column => (string?)column.Header).ToArray(), rowIndex++);

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var values = columns
                    .Select(column => DataExchangeService.FormatValue(column.Property.GetValue(row), options.Culture))
                    .ToArray();

                WriteRow(writer, values, rowIndex++);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = options.SheetName,
        });

        workbookPart.Workbook.Save();

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ImportResult<T>> ImportExcelAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default) where T : new()
    {
        ArgumentNullException.ThrowIfNull(stream);

        var records = ReadExcelRows(stream, cancellationToken);

        return Task.FromResult(ImportCore<T>(records));
    }

    private static void WriteRow(OpenXmlWriter writer, IReadOnlyList<string?> values, uint rowIndex)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i] ?? string.Empty;

            writer.WriteElement(new Cell
            {
                CellReference = ColumnName(i) + rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value)
                {
                    Space = SpaceProcessingModeValues.Preserve,
                }),
            });
        }

        writer.WriteEndElement();
    }

    private static List<CsvRecord> ReadExcelRows(Stream stream, CancellationToken cancellationToken)
    {
        var records = new List<CsvRecord>();

        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart
            ?? throw new FormatException("Excel 缺少 WorkbookPart");

        var firstSheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new FormatException("Excel 中没有工作表");

        if (firstSheet.Id?.Value is not { } relationshipId
            || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
        {
            throw new FormatException("Excel 工作表关系无效");
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        using var reader = OpenXmlReader.Create(worksheetPart);

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                continue;

            var row = (Row)reader.LoadCurrentElement()!;
            var fields = new List<string>();

            foreach (var cell in row.Elements<Cell>())
            {
                var index = ColumnIndex(cell.CellReference?.Value);
                if (index < 0)
                    continue;

                while (fields.Count <= index)
                    fields.Add(string.Empty);

                fields[index] = GetCellText(cell, sharedStrings);
            }

            if (fields.Count == 0)
                continue;

            records.Add(new CsvRecord(fields, (int)(row.RowIndex?.Value ?? (uint)records.Count + 1)));
        }

        return records;
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.Text?.Text ?? string.Empty;

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var index = int.TryParse(cell.CellValue?.InnerText, out var parsed) ? parsed : -1;

            return index >= 0 && sharedStrings is not null && index < sharedStrings.ChildElements.Count
                ? sharedStrings.ChildElements[index].InnerText
                : string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
            return cell.CellValue?.InnerText == "1" ? "true" : "false";

        return cell.CellValue?.InnerText ?? string.Empty;
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return -1;

        var index = 0;

        foreach (var character in reference)
        {
            if (!char.IsAsciiLetter(character))
                break;

            index = index * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return index - 1;
    }

    private static string ColumnName(int index)
    {
        Span<char> buffer = stackalloc char[4];
        var position = buffer.Length;

        do
        {
            buffer[--position] = (char)('A' + index % 26);
            index = index / 26 - 1;
        }
        while (index >= 0);

        return new string(buffer[position..]);
    }
}
