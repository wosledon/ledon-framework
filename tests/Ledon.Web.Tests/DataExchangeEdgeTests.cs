using Ledon.DataExchange;
using Xunit;

namespace Ledon.Web.Tests;

/// <summary>数据交换边界：无表头按属性顺序、空值处理。</summary>
public class DataExchangeEdgeTests
{
    [Fact]
    public async Task Headerless_csv_maps_by_property_order()
    {
        var id = Guid.NewGuid();
        var created = new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var csv = $"{id},12.5,{created:O},note";
        var exchange = new DataExchangeService(new DataExchangeOptions { IncludeHeader = false });

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var result = await exchange.ImportCsvAsync<ExchangeRow>(stream);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var row = Assert.Single(result.Rows);
        Assert.Equal(id, row.Id);
        Assert.Equal(12.5m, row.Amount);
        Assert.Equal(created, row.CreatedAt);
        Assert.Equal("note", row.Note);
    }

    [Fact]
    public async Task Empty_values_keep_value_type_default_and_null_reference()
    {
        var id = Guid.NewGuid();
        var csv = $"订单号,金额,下单时间,备注\n{id},,, \n";
        var exchange = new DataExchangeService(new DataExchangeOptions());

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var result = await exchange.ImportCsvAsync<ExchangeRow>(stream);

        Assert.True(result.Success);
        var row = Assert.Single(result.Rows);
        Assert.Equal(0m, row.Amount);
        Assert.Equal(default, row.CreatedAt);
        Assert.Null(row.Note);
    }
}
