using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;

namespace Ledon.Web.Tests;

/// <summary>Redis 在线注册表边界：连接登记/摘除、在线查询、Hub 作用域。</summary>
public class SignalRRedisEdgeTests
{
    [Fact]
    public void Add_writes_connection_user_and_ttl()
    {
        var (registry, database, _) = BuildRegistry();

        registry.Add(typeof(ChatHub), "conn-1", "alice");

        database.Verify(d => d.HashSet(
            It.Is<RedisKey>(key => key.ToString().EndsWith("ChatHub:connections", StringComparison.Ordinal)),
            "conn-1", "alice", When.Always, CommandFlags.None), Times.Once);
        database.Verify(d => d.SetAdd(
            It.Is<RedisKey>(key => key.ToString().EndsWith("ChatHub:user:alice", StringComparison.Ordinal)),
            "conn-1", CommandFlags.None), Times.Once);
        database.Verify(d => d.KeyExpire(
            It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), ExpireWhen.Always, CommandFlags.None), Times.Exactly(2));
    }

    [Fact]
    public void Remove_clears_user_set_when_connection_is_found()
    {
        var (registry, database, _) = BuildRegistry("ledon:signalr:online:ChatHub:connections");
        database.Setup(d => d.HashGet(It.IsAny<RedisKey>(), "conn-1", CommandFlags.None)).Returns("alice");

        registry.Remove("conn-1");

        database.Verify(d => d.HashDelete(
            It.IsAny<RedisKey>(), "conn-1", CommandFlags.None), Times.Once);
        database.Verify(d => d.SetRemove(
            It.Is<RedisKey>(key => key.ToString().EndsWith("ChatHub:user:alice", StringComparison.Ordinal)),
            "conn-1", CommandFlags.None), Times.Once);
    }

    [Fact]
    public void Online_and_connection_queries_scan_user_sets()
    {
        var (registry, database, _) = BuildRegistry(
            "ledon:signalr:online:ChatHub:user:alice",
            "ledon:signalr:online:NotifyHub:user:alice");
        database.Setup(d => d.SetLength(It.IsAny<RedisKey>(), CommandFlags.None)).Returns(1);
        database.Setup(d => d.SetMembers(It.IsAny<RedisKey>(), CommandFlags.None))
            .Returns([(RedisValue)"conn-1", (RedisValue)"conn-2"]);

        Assert.True(registry.IsOnline("alice"));
        Assert.Equal(["conn-1", "conn-2"], registry.GetConnections("alice").Order());
    }

    [Fact]
    public void Hub_scoped_query_uses_exact_user_set()
    {
        var (registry, database, _) = BuildRegistry();
        database.Setup(d => d.SetMembers(
                It.Is<RedisKey>(key => key.ToString() == "ledon:signalr:online:ChatHub:user:alice"),
                CommandFlags.None))
            .Returns([(RedisValue)"conn-1"]);

        Assert.Equal(["conn-1"], registry.GetConnections(typeof(ChatHub), "alice"));
    }

    [Fact]
    public void Invalid_identifiers_are_rejected()
    {
        var (registry, _, _) = BuildRegistry();

        Assert.Throws<ArgumentException>(() => registry.Remove(""));
        Assert.Throws<ArgumentException>(() => registry.IsOnline(""));
        Assert.Throws<ArgumentException>(() => registry.GetConnections(""));
    }

    private static (RedisOnlineUserRegistry Registry, Mock<IDatabase> Database, Mock<IConnectionMultiplexer> Multiplexer)
        BuildRegistry(params string[] keys)
    {
        var database = new Mock<IDatabase>();
        var server = new Mock<IServer>();
        server
            .Setup(s => s.KeysAsync(
                It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Returns(Keys(keys));

        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        multiplexer.Setup(m => m.GetServers()).Returns([server.Object]);

        var registry = new RedisOnlineUserRegistry(
            multiplexer.Object,
            new LedonOnlineUserRegistryRedisOptions());

        return (registry, database, multiplexer);
    }

    private static async IAsyncEnumerable<RedisKey> Keys(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            await Task.Yield();
            yield return key;
        }
    }

    private sealed class ChatHub;
}
