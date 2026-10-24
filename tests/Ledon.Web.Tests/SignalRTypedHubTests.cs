using Ledon.Testing;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;/// <summary>
/// SignalR 强类型 Hub 与在线注册表测试：
/// <c>Hub&lt;T&gt;</c> 派生类型应被自动发现并映射（生成代码与手写强类型客户端都用它）；
/// 在线用户注册表按 Hub 类型登记连接（多实例部署时接 Redis backplane 共享）。
/// </summary>
public class SignalRTypedHubTests
{
    [Fact]
    public void Strongly_typed_hub_is_discovered_like_plain_hub()
    {
        var services = new ServiceCollection();
        services.AddLedonSignalR();
        using var provider = services.BuildServiceProvider();

        var discovered = provider.GetRequiredService<DiscoveredHubs>().Hubs
            .Select(type => type.FullName)
            .ToArray();

        Assert.Contains(typeof(TypedChatHub).FullName!, discovered);
        Assert.Contains(typeof(ChatHub).FullName!, discovered);
    }

    [Fact]
    public async Task Typed_hub_is_mapped_by_convention()
    {
        await using var host = await LedonWebTestHost.StartAsync(
            configure: builder => builder.Services.AddLedonSignalR(),
            configureApp: app => app.MapLedonSignalRHubs());

        var routes = host.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .Select(endpoint => (endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern?.RawText)
            .ToArray();

        // TypedChatHub → /hubs/typed-chat
        Assert.Contains(routes, pattern => pattern!.Contains("typed-chat", StringComparison.Ordinal));
    }

    [Fact]
    public void Online_registry_tracks_connections_per_hub_type()
    {
        var services = new ServiceCollection();
        services.AddLedonOnlineUserRegistry();
        using var provider = services.BuildServiceProvider();
        var online = provider.GetRequiredService<IOnlineUserRegistry>();

        online.Add(typeof(TypedChatHub), "conn-1", "user-a");
        online.Add(typeof(TypedChatHub), "conn-2", "user-a");   // 同一用户两个连接
        online.Add(typeof(ChatHub), "conn-3", "user-b");

        Assert.True(online.IsOnline("user-a"));
        Assert.True(online.IsOnline("user-b"));
        Assert.False(online.IsOnline("user-c"));

        Assert.Equal(2, online.GetConnections("user-a").Count);
        Assert.Equal(2, online.GetConnections(typeof(TypedChatHub), "user-a").Count);

        online.Remove("conn-1");
        Assert.True(online.IsOnline("user-a"));   // 还有一个连接
        online.Remove("conn-2");
        Assert.False(online.IsOnline("user-a"));
    }
}

// ---------- 夹具 ----------

/// <summary>强类型 Hub 客户端接口（Hub&lt;T&gt; 的 T）。</summary>
public interface ITypedChatClient
{
    Task Receive(string message);
}

/// <summary>强类型 Hub：继承 Hub&lt;T&gt;，验证发现与映射兼容泛型基类。</summary>
public class TypedChatHub : Hub<ITypedChatClient>
{
    public Task Send(string message) => Clients.All.Receive(message);
}
