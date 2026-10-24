using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Ledon.Security;

/// <summary>当前用户访问器（Web 请求上下文；非 Web 场景下为匿名）。</summary>
public interface ICurrentUser
{
    /// <summary>是否已认证。</summary>
    bool IsAuthenticated { get; }

    /// <summary>用户标识（nameidentifier claim），未认证为 null。</summary>
    string? UserId { get; }

    /// <summary>用户名，未认证为 null。</summary>
    string? UserName { get; }

    /// <summary>是否拥有指定权限。</summary>
    bool HasPermission(string permission);

    /// <summary>当前用户的全部权限。</summary>
    IReadOnlyList<string> Permissions { get; }
}

internal sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    private readonly string _permissionClaimType;

    public CurrentUser(IHttpContextAccessor accessor, LedonSecurityOptions options)
    {
        _accessor = accessor;
        _permissionClaimType = options.PermissionClaimType;
    }

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? UserId => Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public string? UserName => Principal?.Identity?.Name;

    public bool HasPermission(string permission) =>
        Principal?.HasClaim(_permissionClaimType, permission) == true;

    public IReadOnlyList<string> Permissions =>
        Principal?.FindAll(_permissionClaimType).Select(claim => claim.Value).ToArray() ?? [];
}
