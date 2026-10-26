using System.Collections.Concurrent;
using System.Security.Claims;

namespace Ledon.Security.Authorization;

/// <summary>RBAC 授权选项。</summary>
public sealed class LedonAuthorizationOptions
{
    /// <summary>权限 claim 类型，默认 permission（与 Ledon.Security 一致）。</summary>
    public string PermissionClaimType { get; set; } = "permission";

    /// <summary>用户 ID claim 类型，默认 nameidentifier。</summary>
    public string UserIdClaimType { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>租户 claim 类型，默认 tenant_id。</summary>
    public string TenantClaimType { get; set; } = "tenant_id";

    /// <summary>权限查询缓存时长，默认 1 分钟。</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>是否映射 /auth/authorization/* 管理端点，默认 <c>false</c>（安全起见需显式开启）。</summary>
    public bool MapEndpoints { get; set; }

    /// <summary>管理端点是否要求授权，默认 <c>true</c>。</summary>
    public bool RequireAuthorization { get; set; } = true;
}

/// <summary>角色。</summary>
public sealed record AuthorizationRole(
    string Name,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset CreatedAtUtc);

/// <summary>角色与用户角色分配存储（默认内存；可替换为数据库）。</summary>
public interface IRoleStore
{
    Task<AuthorizationRole?> FindAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuthorizationRole>> ListAsync(CancellationToken cancellationToken = default);

    Task AddOrUpdateAsync(AuthorizationRole role, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken cancellationToken = default);

    Task AssignAsync(string userId, string roleName, CancellationToken cancellationToken = default);

    Task<bool> RemoveAssignmentAsync(string userId, string roleName, CancellationToken cancellationToken = default);
}

/// <summary>内存角色存储。</summary>
public sealed class InMemoryRoleStore : IRoleStore
{
    private readonly ConcurrentDictionary<string, AuthorizationRole> _roles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _assignments =
        new(StringComparer.Ordinal);

    public Task<AuthorizationRole?> FindAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_roles.TryGetValue(name, out var role) ? role : null);

    public Task<IReadOnlyList<AuthorizationRole>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuthorizationRole>>(
            _roles.Values.OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task AddOrUpdateAsync(AuthorizationRole role, CancellationToken cancellationToken = default)
    {
        _roles[role.Name] = role;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_roles.TryRemove(name, out _))
            return Task.FromResult(false);

        foreach (var (_, roles) in _assignments)
            roles.TryRemove(name, out _);

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_assignments.TryGetValue(userId, out var roles)
            ? roles.Keys.OrderBy(role => role, StringComparer.OrdinalIgnoreCase).ToArray()
            : []);

    public Task AssignAsync(string userId, string roleName, CancellationToken cancellationToken = default)
    {
        if (!_roles.ContainsKey(roleName))
            throw new KeyNotFoundException($"角色不存在：{roleName}");

        var roles = _assignments.GetOrAdd(
            userId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));

        roles.TryAdd(roleName, 0);
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAssignmentAsync(string userId, string roleName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_assignments.TryGetValue(userId, out var roles) && roles.TryRemove(roleName, out _));
}

/// <summary>权限求值器。</summary>
public interface IPermissionEvaluator
{
    Task<bool> HasPermissionAsync(
        string userId,
        string permission,
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetPermissionsAsync(
        string userId,
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>清空权限缓存（角色/分配变更后调用）。</summary>
    void Invalidate();
}
