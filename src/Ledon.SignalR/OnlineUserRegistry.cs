using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SignalR 在线用户注册表：记录"哪个用户有哪些连接"（进程内）。
/// 用途：给指定用户推送、查询在线状态、踢下线。
/// <para>
/// 多实例部署的边界：进程内注册表只覆盖本实例的连接——跨实例推送走
/// <c>IHubContext&lt;T&gt;</c>（配 Redis backplane 时自动广播到所有实例）；
/// 需要全局在线视图时把本接口换成 Redis/数据库实现（注册替换即可）。
/// </para>
/// </summary>
public interface IOnlineUserRegistry
{
    /// <summary>登记一个连接（Hub 连接建立时调用）。</summary>
    void Add(Type hubType, string connectionId, string? userId);

    /// <summary>注销一个连接（Hub 连接断开时调用）。</summary>
    void Remove(string connectionId);

    /// <summary>用户是否在线（至少一个存活连接）。</summary>
    bool IsOnline(string userId);

    /// <summary>用户的全部连接 ID。</summary>
    IReadOnlyList<string> GetConnections(string userId);

    /// <summary>用户在当前 Hub 上的连接 ID。</summary>
    IReadOnlyList<string> GetConnections(Type hubType, string userId);
}

/// <summary><see cref="IOnlineUserRegistry"/> 的进程内实现（线程安全）。</summary>
internal sealed class OnlineUserRegistry : IOnlineUserRegistry
{
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();

    public void Add(Type hubType, string connectionId, string? userId)
    {
        ArgumentNullException.ThrowIfNull(hubType);
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        _connections[connectionId] = new ConnectionEntry(hubType, userId);
    }

    public void Remove(string connectionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        _connections.TryRemove(connectionId, out _);
    }

    public bool IsOnline(string userId) =>
        _connections.Values.Any(entry => string.Equals(entry.UserId, userId, StringComparison.Ordinal));

    public IReadOnlyList<string> GetConnections(string userId) =>
        _connections
            .Where(pair => string.Equals(pair.Value.UserId, userId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

    public IReadOnlyList<string> GetConnections(Type hubType, string userId) =>
        _connections
            .Where(pair => pair.Value.HubType == hubType
                && string.Equals(pair.Value.UserId, userId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

    private sealed record ConnectionEntry(Type HubType, string? UserId);
}

/// <summary>在线注册表的 DI 扩展。</summary>
public static class OnlineUserRegistryExtensions
{
    /// <summary>注册在线用户注册表（单例）。</summary>
    public static IServiceCollection AddLedonOnlineUserRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOnlineUserRegistry, OnlineUserRegistry>();
        return services;
    }
}
