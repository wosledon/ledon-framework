using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledon.Security;

/// <summary>API Key 认证处理器：从请求头读取 Key，命中配置后建立身份。</summary>
internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 处理器只能注入标准服务，业务配置从请求服务里取
        var apiKey = Context.RequestServices.GetRequiredService<LedonSecurityOptions>().ApiKey;

        if (apiKey.Keys.Count == 0)
            return Task.FromResult(AuthenticateResult.Fail("未配置 API Key"));

        if (!Request.Headers.TryGetValue(apiKey.HeaderName, out var provided) || provided.Count == 0)
            return Task.FromResult(AuthenticateResult.Fail("缺少 API Key"));

        var key = provided[0];
        if (string.IsNullOrEmpty(key) || !apiKey.Keys.TryGetValue(key, out var owner) || owner is null)
            return Task.FromResult(AuthenticateResult.Fail("API Key 无效"));

        string claimType = System.Security.Claims.ClaimTypes.Name;
        var claims = new List<System.Security.Claims.Claim>
        {
            new(claimType, owner),
            new("apikey", key),
        };

        var identity = new ClaimsIdentity(claims, SchemeName, System.Security.Claims.ClaimTypes.Name, System.Security.Claims.ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
