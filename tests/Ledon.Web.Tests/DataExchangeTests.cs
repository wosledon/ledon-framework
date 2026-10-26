using Ledon.DataExchange;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>CSV/Excel 导入导出：引号、类型映射、错误行与 DI。</summary>
public class DataExchangeTests
{
    [Fact]
    public async Task Csv_roundtrip_handles_quotes_newlines_and_types()
    {
        var exchange = new DataExchangeService(new DataExchangeOptions());
        var id = Guid.NewGuid();
        var rows = new[]
        {
            new ExchangeRow
            {
                Id = id,
                Amount = 12.50m,
                CreatedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                Note = "含,逗号 \"引号\" 和\n换行",
            },
        };

        await using var stream = new MemoryStream();
        await exchange.ExportCsvAsync(stream, rows);

        stream.Position = 0;
        var result = await exchange.ImportCsvAsync<ExchangeRow>(stream);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var row = Assert.Single(result.Rows);
        Assert.Equal(id, row.Id);
        Assert.Equal(12.50m, row.Amount);
        Assert.Equal(rows[0].CreatedAt, row.CreatedAt);
        Assert.Equal(rows[0].Note, row.Note);
    }

    [Fact]
    public async Task Csv_import_reports_row_and_column_errors()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var csv = $"""
            订单号,金额,备注
            {id1},10,ok
            {id2},not-a-number,bad
            {id3},20,ok2
            """;

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var exchange = new DataExchangeService(new DataExchangeOptions());
        var result = await exchange.ImportCsvAsync<ExchangeRow>(stream);

        Assert.Equal(2, result.Rows.Count);
        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.RowNumber);
        Assert.Equal("金额", error.Column);
    }

    [Fact]
    public async Task Excel_roundtrip_preserves_values()
    {
        var exchange = new DataExchangeService(new DataExchangeOptions { SheetName = "Orders" });
        var rows = new[]
        {
            new ExchangeRow { Id = Guid.NewGuid(), Amount = 3.14m, CreatedAt = DateTimeOffset.UtcNow, Note = "x" },
            new ExchangeRow { Id = Guid.NewGuid(), Amount = -1m, CreatedAt = DateTimeOffset.UtcNow, Note = null },
        };

        await using var stream = new MemoryStream();
        await exchange.ExportExcelAsync(stream, rows);

        stream.Position = 0;
        var result = await exchange.ImportExcelAsync<ExchangeRow>(stream);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(rows[0].Id, result.Rows[0].Id);
        Assert.Equal(3.14m, result.Rows[0].Amount);
        Assert.Null(result.Rows[1].Note);
    }

    [Fact]
    public void Di_registration_resolves_exchange()
    {
        var services = new ServiceCollection();
        services.AddLedonDataExchange();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IDataExchange>());
    }
}

public sealed class ExchangeRow
{
    [DataColumn("订单号", 1)]
    public Guid Id { get; set; }

    [DataColumn("金额", 2)]
    public decimal Amount { get; set; }

    [DataColumn("下单时间", 3)]
    public DateTimeOffset CreatedAt { get; set; }

    [DataColumn("备注", 4)]
    public string? Note { get; set; }

    [DataIgnore]
    public string? Internal { get; set; }
}
