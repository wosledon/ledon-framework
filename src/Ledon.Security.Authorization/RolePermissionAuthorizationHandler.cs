using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Ledon.Security.Authorization;

/// <summary>角色权限处理器：把 RBAC 权限接入现有 PermissionRequirement 策略。</summary>
public sealed class RolePermissionAuthorizationHandler(
    IPermissionEvaluator evaluator,
    LedonAuthorizationOptions options) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userId = context.User.FindFirst(options.UserIdClaimType)?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(userId))
            return;

        var tenantId = context.User.FindFirst(options.TenantClaimType)?.Value;

        if (await evaluator.HasPermissionAsync(userId, requirement.Permission, tenantId, CancellationToken.None).ConfigureAwait(false))
            context.Succeed(requirement);
    }
}

/// <summary>无 Ledon.Security 时也能按权限名动态生成策略。</summary>
public sealed class RolePermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
        GetPolicyCoreAsync(policyName);

    private async Task<AuthorizationPolicy?> GetPolicyCoreAsync(string policyName)
    {
        var configured = await _fallback.GetPolicyAsync(policyName).ConfigureAwait(false);

        return configured ?? new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();
    }
}
