using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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

/// <summary>
/// Hub 基类：连接建立/断开时自动登记在线状态，业务 Hub 不用手写 OnConnectedAsync。
/// <code>
/// public class ChatHub : OnlineAwareHub   // 在线状态自动维护
/// {
///     public Task Send(string text) => Clients.All.SendAsync("recv", text);
/// }
/// </code>
/// 用户标识默认取 <see cref="HubConnectionContext.UserIdentifier"/>（登录用户）；
/// 匿名连接 userId 为 null（仍会登记，便于按 connectionId 查）。
/// </summary>
public abstract class OnlineAwareHub : Hub
{
    private IOnlineUserRegistry? _registry;

    /// <summary>在线注册表（懒解析：Hub 可能是每连接构造的，构造期容器还没就绪）。</summary>
    protected virtual IOnlineUserRegistry Registry =>
        _registry ??= Context.GetHttpContext()?.RequestServices.GetService<IOnlineUserRegistry>()
            ?? throw new InvalidOperationException(
                "未注册 IOnlineUserRegistry。调用 AddLedonOnlineUserRegistry()（AddLedonSignalR 已默认注册）。");

    public override Task OnConnectedAsync()
    {
        Registry.Add(GetType(), Context.ConnectionId, Context.UserIdentifier);

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Registry.Remove(Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
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

    /// <summary>
    /// 用 Redis 实现在线注册表（多实例部署的全局在线视图）。
    /// 覆盖 <see cref="AddLedonOnlineUserRegistry"/> 注册的进程内实现。
    /// <code>
    /// builder.Services.AddLedonSignalR(o =&gt; o.RedisConnectionString = "localhost:6379");
    /// // ↑ 内部会自动调用本方法
    /// </code>
    /// </summary>
    public static IServiceCollection AddLedonOnlineUserRegistryRedis(
        this IServiceCollection services,
        Action<LedonOnlineUserRegistryRedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonOnlineUserRegistryRedisOptions();
        configure(options);

        services.Replace(ServiceDescriptor.Singleton<IOnlineUserRegistry>(provider =>
            new RedisOnlineUserRegistry(
                StackExchange.Redis.ConnectionMultiplexer.Connect(options.RedisConnectionString),
                options)));

        return services;
    }
}

/// <summary>Redis 在线注册表选项。</summary>
public sealed class LedonOnlineUserRegistryRedisOptions
{
    /// <summary>Redis 连接串。</summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>key 前缀（多套应用共享 Redis 时区分），默认 ledon:signalr:online:。</summary>
    public string KeyPrefix { get; set; } = "ledon:signalr:online:";

    /// <summary>
    /// 连接登记的有效期，默认 5 分钟。它是兜底：连接异常断开（进程被杀）时
    /// OnDisconnectedAsync 不会执行，靠过期自动清理，避免在线状态永远停在"在线"。
    /// </summary>
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// 基于 Redis 的在线注册表：Hash 存"连接 → 用户"，Set 存"用户 → 连接集合"。
/// 多实例部署下全局在线视图、跨实例踢下线都成立。
/// </summary>
internal sealed class RedisOnlineUserRegistry : IOnlineUserRegistry
{
    /// <summary>连接 → 用户（Hash field 为 connectionId）。</summary>
    private const string ConnectionsKey = "connections";

    private readonly StackExchange.Redis.IConnectionMultiplexer _multiplexer;
    private readonly LedonOnlineUserRegistryRedisOptions _options;

    public RedisOnlineUserRegistry(
        StackExchange.Redis.IConnectionMultiplexer multiplexer,
        LedonOnlineUserRegistryRedisOptions options)
    {
        _multiplexer = multiplexer;
        _options = options;
    }

    private StackExchange.Redis.IDatabase Database => _multiplexer.GetDatabase();

    private string HubKey(Type hubType) => $"{_options.KeyPrefix}{hubType.Name}:";

    private string UserSetKey(Type hubType, string userId) => $"{HubKey(hubType)}user:{userId}";

    public void Add(Type hubType, string connectionId, string? userId)
    {
        ArgumentNullException.ThrowIfNull(hubType);
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        var db = Database;

        // 连接 → 用户 + 用户 → 连接集合（两处都要写，查询才不用扫全表）
        db.HashSet($"{HubKey(hubType)}{ConnectionsKey}", connectionId, userId ?? "");
        if (userId is not null)
            db.SetAdd(UserSetKey(hubType, userId), connectionId);

        db.KeyExpire(UserSetKey(hubType, userId ?? ""), _options.EntryTtl);
        db.KeyExpire($"{HubKey(hubType)}{ConnectionsKey}", _options.EntryTtl);
    }

    public void Remove(string connectionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        var db = Database;
        var hubKeyPrefixLength = _options.KeyPrefix.Length;

        // 先查出这条连接属于哪个用户/哪个 Hub，才能从用户集合里摘掉
        foreach (var server in _multiplexer.GetServers())
        {
            var found = false;

            foreach (var key in server.KeysAsync(pattern: $"{_options.KeyPrefix}*:{ConnectionsKey}").ToBlockingEnumerable())
            {
                var userId = db.HashGet(key, connectionId);
                if (userId.IsNull)
                    continue;

                db.HashDelete(key, connectionId);

                if (!userId.IsNull)
                {
                    // key = {prefix}{hub}:connections → 用户集合 key = {prefix}{hub}:user:{userId}
                    var hubPart = key.ToString().AsSpan(hubKeyPrefixLength);
                    var colon = hubPart.IndexOf(':');
                    if (colon > 0)
                        db.SetRemove($"{_options.KeyPrefix}{hubPart[..colon]}:user:{userId}", connectionId);
                }

                found = true;
                break;
            }

            if (found)
                return;
        }
    }

    public bool IsOnline(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        foreach (var server in _multiplexer.GetServers())
            foreach (var key in server.KeysAsync(pattern: $"{_options.KeyPrefix}*:user:{userId}").ToBlockingEnumerable())
                if (Database.SetLength(key) > 0)
                    return true;

        return false;
    }

    public IReadOnlyList<string> GetConnections(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var server in _multiplexer.GetServers())
            foreach (var key in server.KeysAsync(pattern: $"{_options.KeyPrefix}*:user:{userId}").ToBlockingEnumerable())
                foreach (var member in Database.SetMembers(key))
                    result.Add(member.ToString());

        return result.ToArray();
    }

    public IReadOnlyList<string> GetConnections(Type hubType, string userId)
    {
        ArgumentNullException.ThrowIfNull(hubType);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        return Database.SetMembers(UserSetKey(hubType, userId))
            .Select(value => value.ToString())
            .ToArray();
    }
}
