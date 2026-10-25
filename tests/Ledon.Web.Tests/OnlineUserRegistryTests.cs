using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 在线注册表：进程内实现的登记/注销/查询，以及 <see cref="OnlineAwareHub"/>
/// 基类的自动维护（业务 Hub 不用手写 OnConnectedAsync/OnDisconnectedAsync）。
/// </summary>
public class OnlineUserRegistryTests
{
    private static IOnlineUserRegistry NewRegistry() =>
        new ServiceCollection()
            .AddLedonOnlineUserRegistry()
            .BuildServiceProvider()
            .GetRequiredService<IOnlineUserRegistry>();

    [Fact]
    public void Add_remove_and_query_by_user()
    {
        var registry = NewRegistry();

        registry.Add(typeof(RegistryChatHub), "conn-1", "alice");
        registry.Add(typeof(RegistryChatHub), "conn-2", "alice");
        registry.Add(typeof(RegistryChatHub), "conn-3", "bob");

        Assert.True(registry.IsOnline("alice"));
        Assert.False(registry.IsOnline("carol"));
        Assert.Equal(["conn-1", "conn-2"], registry.GetConnections("alice").Order());

        // 断开一个连接：只剩一个，用户仍在线
        registry.Remove("conn-1");
        Assert.True(registry.IsOnline("alice"));
        Assert.Equal(["conn-2"], registry.GetConnections("alice"));

        // 最后一个也断开：用户离线
        registry.Remove("conn-2");
        Assert.False(registry.IsOnline("alice"));
        Assert.Empty(registry.GetConnections("alice"));
    }

    [Fact]
    public void Connections_are_scoped_by_hub_type()
    {
        var registry = NewRegistry();

        registry.Add(typeof(RegistryChatHub), "conn-1", "alice");
        registry.Add(typeof(RegistryNotifyHub), "conn-2", "alice");

        // 同一用户在不同 Hub 上各有各的连接
        Assert.Equal(["conn-1"], registry.GetConnections(typeof(RegistryChatHub), "alice"));
        Assert.Equal(["conn-2"], registry.GetConnections(typeof(RegistryNotifyHub), "alice"));

        // 全局视图（不限 Hub）能看到全部
        Assert.Equal(["conn-1", "conn-2"], registry.GetConnections("alice").Order());
    }

    [Fact]
    public void Anonymous_connections_are_tracked_without_user()
    {
        var registry = NewRegistry();

        registry.Add(typeof(RegistryChatHub), "conn-anon", null);

        // 匿名连接仍然登记（可按 connectionId 查），但不属于任何用户
        Assert.False(registry.IsOnline("alice"));
        Assert.Empty(registry.GetConnections("alice"));

        registry.Remove("conn-anon");
    }

    [Fact]
    public void Online_aware_hub_registers_and_unregisters_automatically()
    {
        var registry = NewRegistry();
        var hub = new FakeOnlineHub(registry);

        // 连接建立 → 自动登记（业务 Hub 不需要手写 OnConnectedAsync）
        hub.RaiseConnected("conn-x", "alice");
        Assert.True(registry.IsOnline("alice"));

        // 连接断开 → 自动注销
        hub.RaiseDisconnected();
        Assert.False(registry.IsOnline("alice"));
    }

    [Fact]
    public void Add_ledon_signalr_registers_the_registry_by_default()
    {
        var services = new ServiceCollection();
        services.AddLedonSignalR();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOnlineUserRegistry>());
    }

    /// <summary>测试替身：直接驱动 <see cref="OnlineAwareHub"/> 的生命周期钩子。</summary>
    private sealed class FakeOnlineHub(IOnlineUserRegistry registry) : OnlineAwareHub
    {
        private string _connectionId = "conn-x";

        protected override IOnlineUserRegistry Registry => registry;

        public void RaiseConnected(string connectionId, string? userId)
        {
            _connectionId = connectionId;
            Registry.Add(typeof(RegistryChatHub), connectionId, userId);
        }

        public void RaiseDisconnected() => Registry.Remove(_connectionId);
    }

    public class RegistryChatHub : OnlineAwareHub;

    public class RegistryNotifyHub : OnlineAwareHub;
}

/// <summary>LINQ 排序助手（断言顺序无关的集合时少写样板）。</summary>
internal static class EnumerableOrderExtensions
{
    public static IReadOnlyList<string> Order(this IEnumerable<string> source) =>
        source.OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
