using Microsoft.Extensions.Caching.Memory;

namespace Ledon.Security.Authorization;

/// <summary>带缓存的权限求值器（角色权限并集 + 通配符匹配）。</summary>
public sealed class CachingPermissionEvaluator(
    IRoleStore store,
    IMemoryCache cache,
    LedonAuthorizationOptions options,
    TimeProvider timeProvider) : IPermissionEvaluator
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    public async Task<bool> HasPermissionAsync(
        string userId,
        string permission,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var permissions = await GetPermissionsAsync(userId, tenantId, cancellationToken).ConfigureAwait(false);

        return permissions.Any(granted => Matches(granted, permission));
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        string userId,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var cacheKey = $"{userId}:{tenantId ?? "-"}";

        if (cache.TryGetValue<IReadOnlyList<string>>(cacheKey, out var cached) && cached is not null)
            return cached;

        var roles = await store.GetRolesAsync(userId, cancellationToken).ConfigureAwait(false);
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var roleName in roles)
        {
            var role = await store.FindAsync(roleName, cancellationToken).ConfigureAwait(false);

            if (role is not null)
                permissions.UnionWith(role.Permissions);
        }

        var result = permissions.OrderBy(permission => permission, StringComparer.Ordinal).ToArray();
        _keys.TryAdd(cacheKey, 0);
        cache.Set(cacheKey, result, timeProvider.GetUtcNow() + options.CacheDuration);

        return result;
    }

    public void Invalidate()
    {
        foreach (var key in _keys.Keys)
        {
            cache.Remove(key);
            _keys.TryRemove(key, out _);
        }
    }

    /// <summary>通配符匹配：<c>orders.*</c> 匹配 orders.read/create；<c>*</c> 匹配全部。</summary>
    public static bool Matches(string granted, string requested)
    {
        if (string.Equals(granted, requested, StringComparison.Ordinal))
            return true;

        if (!granted.Contains('*'))
            return false;

        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (valueIndex < requested.Length)
        {
            if (patternIndex < granted.Length
                && (granted[patternIndex] == requested[valueIndex]))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < granted.Length && granted[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                matchIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++matchIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < granted.Length && granted[patternIndex] == '*')
            patternIndex++;

        return patternIndex == granted.Length;
    }
}

/// <summary>角色管理服务。</summary>
public sealed class RoleAuthorizationService(
    IRoleStore store,
    IPermissionEvaluator evaluator,
    TimeProvider timeProvider)
{
    public Task<IReadOnlyList<AuthorizationRole>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    public async Task<AuthorizationRole> CreateRoleAsync(
        string name,
        IEnumerable<string>? permissions = null,
        CancellationToken cancellationToken = default)
    {
        name = ValidateRoleName(name);

        if (await store.FindAsync(name, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"角色已存在：{name}");

        var role = new AuthorizationRole(
            name,
            NormalizePermissions(permissions),
            timeProvider.GetUtcNow());

        await store.AddOrUpdateAsync(role, cancellationToken).ConfigureAwait(false);
        evaluator.Invalidate();

        return role;
    }

    public async Task<AuthorizationRole> SetPermissionsAsync(
        string name,
        IEnumerable<string> permissions,
        CancellationToken cancellationToken = default)
    {
        name = ValidateRoleName(name);
        var existing = await store.FindAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"角色不存在：{name}");

        var role = existing with { Permissions = NormalizePermissions(permissions) };
        await store.AddOrUpdateAsync(role, cancellationToken).ConfigureAwait(false);
        evaluator.Invalidate();

        return role;
    }

    public async Task<bool> DeleteRoleAsync(string name, CancellationToken cancellationToken = default)
    {
        var removed = await store.RemoveAsync(ValidateRoleName(name), cancellationToken).ConfigureAwait(false);

        if (removed)
            evaluator.Invalidate();

        return removed;
    }

    public async Task AssignRoleAsync(
        string userId,
        string roleName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await store.AssignAsync(userId, ValidateRoleName(roleName), cancellationToken).ConfigureAwait(false);
        evaluator.Invalidate();
    }

    public async Task<bool> RemoveRoleAsync(
        string userId,
        string roleName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var removed = await store.RemoveAssignmentAsync(userId, ValidateRoleName(roleName), cancellationToken).ConfigureAwait(false);

        if (removed)
            evaluator.Invalidate();

        return removed;
    }

    public Task<IReadOnlyList<string>> GetUserRolesAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return store.GetRolesAsync(userId, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetUserPermissionsAsync(
        string userId,
        string? tenantId = null,
        CancellationToken cancellationToken = default) =>
        evaluator.GetPermissionsAsync(userId, tenantId, cancellationToken);

    public Task<bool> HasPermissionAsync(
        string userId,
        string permission,
        string? tenantId = null,
        CancellationToken cancellationToken = default) =>
        evaluator.HasPermissionAsync(userId, permission, tenantId, cancellationToken);

    private static string ValidateRoleName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();

        if (trimmed.Length is < 2 or > 64)
            throw new ArgumentException("角色名长度必须为 2-64 个字符", nameof(name));

        return trimmed;
    }

    private static IReadOnlyCollection<string> NormalizePermissions(IEnumerable<string>? permissions) =>
        (permissions ?? [])
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();
}
