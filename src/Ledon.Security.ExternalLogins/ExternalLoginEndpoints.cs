using Ledon.Security;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ledon.Security.ExternalLogins;

/// <summary>外部登录端点：challenge 跳转 + callback 换取框架令牌。</summary>
public sealed class ExternalLoginEndpoints(
    IEnumerable<IExternalLoginProvider> providers,
    ExternalLoginStateProtector stateProtector,
    IExternalUserResolver userResolver,
    IJwtTokenGenerator jwt,
    IRefreshTokenService refresh,
    LedonSecurityOptions securityOptions) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/auth/external");

        group.MapGet("/{provider}/challenge", (
            string provider,
            string? returnUrl,
            HttpContext context) =>
        {
            var loginProvider = FindProvider(providers, provider);

            if (loginProvider is null)
                return Results.NotFound(new { error = $"未配置外部登录提供程序：{provider}" });

            var state = stateProtector.Protect(loginProvider.Name, returnUrl);
            var redirectUri = CallbackUri(context, loginProvider.Name);

            return Results.Redirect(loginProvider.BuildAuthorizationUrl(state, redirectUri));
        });

        group.MapGet("/{provider}/callback", async (
            string provider,
            string? code,
            string? state,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var loginProvider = FindProvider(providers, provider);

            if (loginProvider is null)
                return Results.NotFound(new { error = $"未配置外部登录提供程序：{provider}" });

            if (string.IsNullOrWhiteSpace(code))
                return Results.BadRequest(new { error = "缺少 code" });

            ExternalLoginState statePayload;

            try
            {
                statePayload = stateProtector.Unprotect(state ?? string.Empty);

                if (!string.Equals(statePayload.Provider, loginProvider.Name, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "state 与提供程序不匹配" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            ExternalLoginUser externalUser;

            try
            {
                externalUser = await loginProvider.ExchangeCodeAsync(
                    code,
                    CallbackUri(context, loginProvider.Name),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    $"外部登录失败：{ex.Message}",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var user = await userResolver.ResolveAsync(externalUser, cancellationToken).ConfigureAwait(false);

            if (user is null)
                return Results.Unauthorized();

            var accessToken = jwt.Generate(user.UserId, user.UserName, user.Permissions);
            var refreshToken = await refresh.IssueAsync(user.UserId, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new TokenResponse(
                accessToken,
                refreshToken,
                (int)securityOptions.Jwt.Expiration.TotalSeconds));
        });
    }

    private static IExternalLoginProvider? FindProvider(
        IEnumerable<IExternalLoginProvider> providers,
        string name) =>
        providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string CallbackUri(HttpContext context, string provider) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}" +
        $"/auth/external/{Uri.EscapeDataString(provider)}/callback";
}
