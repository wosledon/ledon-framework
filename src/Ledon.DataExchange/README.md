# Ledon.DataExchange

CSV 与 Excel（.xlsx）流式导入导出，自动按属性映射表头，逐行收集转换错误。

```csharp
public sealed class OrderRow
{
    [DataColumn("订单号", 1)] public Guid Id { get; set; }
    [DataColumn("金额", 2)] public decimal Amount { get; set; }
    [DataColumn("下单时间", 3)] public DateTimeOffset CreatedAt { get; set; }
    [DataColumn("备注", 4)] public string? Note { get; set; }
    [DataIgnore] public string? Internal { get; set; }
}

builder.Services.AddLedonDataExchange();
```

```csharp
// CSV
await exchange.ExportCsvAsync(stream, orders);
var result = await exchange.ImportCsvAsync<OrderRow>(stream);
if (!result.Success)
    foreach (var error in result.Errors)
        Console.WriteLine($"第 {error.RowNumber} 行 {error.Column}: {error.Message}");

// Excel
await exchange.ExportExcelAsync(stream, orders);
var imported = await exchange.ImportExcelAsync<OrderRow>(stream);
```

- CSV 遵循 RFC 4180：引号、双引号转义、字段内换行、UTF-8 BOM；
- Excel 使用 OpenXML 流式写出（`OpenXmlWriter`）与逐行读取（`OpenXmlReader`）；
- 支持 string/数字/布尔/Guid/DateTime/DateTimeOffset/DateOnly/TimeOnly/枚举/byte[]；
- 空值：可空类型写 null，非可空值类型保持默认值；
- 导入错误包含行号、列名与原因，`MaxErrors` 防止坏文件耗尽内存；
- 默认表头、逗号分隔、CRLF；可配置分隔符/引号/编码/工作表名/文化。

完整文档：<https://github.com/wosledon/ledon-framework>
