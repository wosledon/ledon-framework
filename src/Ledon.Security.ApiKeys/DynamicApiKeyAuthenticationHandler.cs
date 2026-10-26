using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledon.Security.ApiKeys;

/// <summary>动态 API Key 认证方案名。</summary>
public static class ApiKeyDefaults
{
    public const string Scheme = "LedonApiKey";
}

/// <summary>从请求头读取动态 API Key 并建立身份。</summary>
internal sealed class DynamicApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var settings = Context.RequestServices.GetRequiredService<ApiKeyManagementOptions>();
        var service = Context.RequestServices.GetRequiredService<ApiKeyService>();

        if (!Request.Headers.TryGetValue(settings.HeaderName, out var provided) || provided.Count == 0)
            return AuthenticateResult.NoResult();

        var key = await service.ValidateAsync(provided[0]!, Context.RequestAborted).ConfigureAwait(false);

        if (key is null)
            return AuthenticateResult.Fail("API Key 无效、已吊销或已过期");

        var permissionClaimType = Context.RequestServices
            .GetService<LedonSecurityOptions>()?.PermissionClaimType ?? "permission";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new(ClaimTypes.Name, key.Name),
            new("apikey", key.Prefix),
        };

        if (!string.IsNullOrEmpty(key.TenantId))
            claims.Add(new Claim("tenant", key.TenantId));

        foreach (var scope in key.Scopes)
            claims.Add(new Claim(permissionClaimType, scope));

        var identity = new ClaimsIdentity(
            claims,
            ApiKeyDefaults.Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyDefaults.Scheme));
    }
}
