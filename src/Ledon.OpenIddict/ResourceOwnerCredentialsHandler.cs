using System.Security.Claims;
using Ledon.Security;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Ledon.OpenIddict;

/// <summary>
/// 密码 / 客户端凭证授权的凭证校验与身份构建：复用框架的 <see cref="ILedonUserValidator"/>，
/// client_credentials 按客户端定义的权限直接签发。
/// 挂在 <see cref="OpenIddictServerEvents.HandleTokenRequestContext"/>（OpenIddict 要求
/// token 请求在此产生身份；GenerateTokenContext 只负责把已建立的身份打进令牌）。
/// </summary>
internal sealed class ResourceOwnerCredentialsHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.HandleTokenRequestContext>
{
    private readonly ILedonUserValidator _validator;
    private readonly LedonOpenIddictOptions _options;
    private readonly LedonSecurityOptions _securityOptions;

    public ResourceOwnerCredentialsHandler(
        ILedonUserValidator validator,
        LedonOpenIddictOptions options,
        LedonSecurityOptions securityOptions)
    {
        _validator = validator;
        _options = options;
        _securityOptions = securityOptions;
    }

    public async ValueTask HandleAsync(OpenIddictServerEvents.HandleTokenRequestContext context)
    {
        var request = context.Request;
        if (request is null)
            return;

        if (string.Equals(request.GrantType, OpenIddictConstants.GrantTypes.Password, StringComparison.Ordinal))
        {
            var userName = request.Username;
            var password = request.Password;

            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
            {
                context.Reject(
                    error: OpenIddictConstants.Errors.InvalidGrant,
                    description: "用户名或密码缺失。");
                return;
            }

            var user = await _validator.ValidateAsync(userName, password, context.CancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                context.Reject(
                    error: OpenIddictConstants.Errors.InvalidGrant,
                    description: "用户名或密码错误。");
                return;
            }

            context.Principal = BuildPrincipal(user.UserId, user.UserName, user.Permissions, request.GetScopes());
            return;
        }

        if (string.Equals(request.GrantType, OpenIddictConstants.GrantTypes.ClientCredentials, StringComparison.Ordinal))
        {
            var client = _options.Clients.FirstOrDefault(candidate => candidate.ClientId == context.ClientId);
            if (client is null)
            {
                context.Reject(
                    error: OpenIddictConstants.Errors.InvalidClient,
                    description: "客户端不存在。");
                return;
            }

            // 服务端身份：subject = client_id，权限来自客户端定义
            context.Principal = BuildPrincipal(client.ClientId, client.DisplayName, client.Permissions, request.GetScopes());
        }
    }

    private ClaimsPrincipal BuildPrincipal(
        string userId,
        string userName,
        IReadOnlyList<string> permissions,
        IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType);

        identity.AddClaim(WithDestination(new Claim(OpenIddictConstants.Claims.Subject, userId)));
        identity.AddClaim(WithDestination(new Claim(OpenIddictConstants.Claims.Name, userName)));

        foreach (var permission in permissions)
            identity.AddClaim(WithDestination(new Claim(_securityOptions.PermissionClaimType, permission)));

        // scope 声明：刷新令牌的签发以此为前提（offline_access）
        identity.SetScopes(scopes);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>声明必须显式设置目的地（destination），否则 OpenIddict 不会把它写进令牌。</summary>
    private static Claim WithDestination(Claim claim)
    {
        claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
        return claim;
    }
}
