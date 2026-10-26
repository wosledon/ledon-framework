namespace Ledon.Security.ExternalLogins;

/// <summary>外部登录提供程序类型。</summary>
public enum ExternalLoginProviderType
{
    /// <summary>标准 OIDC（授权码 + userinfo）。</summary>
    Oidc = 0,

    /// <summary>GitHub OAuth App。</summary>
    GitHub = 1,
}

/// <summary>单个外部登录提供程序配置。</summary>
public sealed class ExternalLoginProviderOptions
{
    /// <summary>提供程序名（URL 中使用，如 <c>github</c> / <c>google</c>）。</summary>
    public required string Name { get; set; }

    /// <summary>提供程序类型。</summary>
    public ExternalLoginProviderType Type { get; set; }

    /// <summary>OAuth Client ID。</summary>
    public required string ClientId { get; set; }

    /// <summary>OAuth Client Secret。</summary>
    public required string ClientSecret { get; set; }

    /// <summary>OIDC 授权端点（Type = Oidc 必填）。</summary>
    public string? AuthorizationEndpoint { get; set; }

    /// <summary>OIDC Token 端点（Type = Oidc 必填）。</summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>OIDC UserInfo 端点（Type = Oidc 必填）。</summary>
    public string? UserInfoEndpoint { get; set; }

    /// <summary>申请的 scope（OIDC 默认 openid profile email；GitHub 默认 read:user user:email）。</summary>
    public string[]? Scopes { get; set; }
}

/// <summary>外部登录总配置。</summary>
public sealed class ExternalLoginOptions
{
    /// <summary>提供程序列表。</summary>
    public List<ExternalLoginProviderOptions> Providers { get; } = [];

    /// <summary>state 有效期，默认 5 分钟。</summary>
    public TimeSpan StateLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Data Protection 用途名，默认 <c>Ledon.Security.ExternalLogins</c>。</summary>
    public string StateProtectionPurpose { get; set; } = "Ledon.Security.ExternalLogins";

    /// <summary>自定义 HTTP 消息处理器（测试注入）。</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}

/// <summary>外部身份信息。</summary>
public sealed record ExternalLoginUser(
    string Provider,
    string Subject,
    string? Email,
    string? Name,
    IReadOnlyDictionary<string, string> Claims);

/// <summary>外部登录提供程序。</summary>
public interface IExternalLoginProvider
{
    /// <summary>提供程序名。</summary>
    string Name { get; }

    /// <summary>构造授权跳转地址。</summary>
    string BuildAuthorizationUrl(string state, string redirectUri);

    /// <summary>用授权码换取外部身份。</summary>
    Task<ExternalLoginUser> ExchangeCodeAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default);
}

/// <summary>把外部身份映射为框架用户（可覆盖：本地用户绑定、自动注册、租户选择等）。</summary>
public interface IExternalUserResolver
{
    Task<LedonUser?> ResolveAsync(ExternalLoginUser externalUser, CancellationToken cancellationToken = default);
}

/// <summary>默认映射：以 <c>provider:subject</c> 作为用户 ID，不授予权限。</summary>
public sealed class DefaultExternalUserResolver : IExternalUserResolver
{
    public Task<LedonUser?> ResolveAsync(
        ExternalLoginUser externalUser,
        CancellationToken cancellationToken = default)
    {
        var userId = $"{externalUser.Provider}:{externalUser.Subject}";
        var name = externalUser.Name ?? externalUser.Email ?? userId;

        return Task.FromResult<LedonUser?>(new LedonUser(userId, name, []));
    }
}

/// <summary>state 载荷（Data Protection 保护，防篡改/过期）。</summary>
public sealed record ExternalLoginState(string Provider, string? ReturnUrl, DateTimeOffset ExpiresAtUtc)
{
    /// <summary>是否已过期。</summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc;
}
