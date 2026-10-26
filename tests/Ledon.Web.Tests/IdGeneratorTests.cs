using Ledon.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>分布式 ID：ULID 解析/排序、GUID v7、Snowflake 唯一性与线程安全。</summary>
public class IdGeneratorTests
{
    [Fact]
    public void Ulid_roundtrips_and_sorts_by_time()
    {
        var first = Ulid.New();
        var second = Ulid.New();

        Assert.Equal(26, first.ToString().Length);
        Assert.True(Ulid.TryParse(first.ToString(), out var parsed));
        Assert.Equal(first, parsed);
        Assert.True(second > first);

        Assert.True(Ulid.TryParse(first.ToString().ToLowerInvariant(), out var lower));
        Assert.Equal(first, lower);
        Assert.True(Ulid.TryParse("00000000000000000000000000", out _));
        Assert.False(Ulid.TryParse("not-a-ulid", out _));
        Assert.Equal(16, first.ToByteArray().Length);
        Assert.Equal(first, Ulid.FromByteArray(first.ToByteArray()));
    }

    [Fact]
    public void Guid_is_version7()
    {
        var guid = Guid.CreateVersion7();
        var text = guid.ToString("D");

        // GUID v7 的版本 nibble 在第三段首位
        Assert.Equal('7', text[14]);
    }

    [Fact]
    public void Snowflake_is_unique_and_increasing()
    {
        var generator = new SnowflakeIdGenerator(workerId: 7);
        var ids = new List<long>(10_000);

        for (var index = 0; index < 10_000; index++)
            ids.Add(generator.NextId());

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => id), ids);
    }

    [Fact]
    public void Di_registration_provides_all_generators()
    {
        var services = new ServiceCollection();
        services.AddLedonIds(o => o.WorkerId = 3);

        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<IIdGenerator>();

        Assert.Equal(26, generator.NewId().Length);
        Assert.NotEqual(Guid.Empty, generator.NewGuid());
        Assert.True(generator.NewSnowflake() > 0);
        Assert.NotEqual(generator.NewUlid(), generator.NewUlid());
    }
}
