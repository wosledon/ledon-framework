using Ledon.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>ID 生成边界：WorkerId 校验、ULID 非法输入、DI 幂等。</summary>
public class IdsEdgeTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1024)]
    public void Worker_id_out_of_range_is_rejected(long workerId)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLedonIds(options => options.WorkerId = workerId));

        Assert.Contains("0-1023", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Snowflake_rejects_invalid_worker_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdGenerator(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdGenerator(1024));
    }

    [Fact]
    public void Ulid_parse_rejects_null_and_invalid_values()
    {
        Assert.False(Ulid.TryParse(null, out _));
        Assert.False(Ulid.TryParse(string.Empty, out _));
        Assert.False(Ulid.TryParse("not-a-ulid", out _));
        Assert.Throws<FormatException>(() => Ulid.Parse("not-a-ulid"));
    }

    [Fact]
    public void Di_registration_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddLedonIds(options => options.WorkerId = 1);
        services.AddLedonIds(options => options.WorkerId = 2);

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IIdGenerator>();
        var second = provider.GetRequiredService<IIdGenerator>();

        Assert.Same(first, second);
    }
}
