namespace Ledon.Security;

/// <summary>JWT 配置。</summary>
public sealed class JwtOptions
{
    /// <summary>签发者。</summary>
    public string Issuer { get; set; } = "ledon";

    /// <summary>受众。</summary>
    public string Audience { get; set; } = "ledon-app";

    /// <summary>签名密钥（至少 32 字符；框架默认值是公开的开发密钥，仅用于本地试跑）。</summary>
    /// <remarks>
    /// <c>AddLedonSecurity</c> 在启动时会校验：仍是默认值或长度不足 32 直接抛异常。
    /// 生产环境务必从配置中心/密钥管理注入——用这个公开密钥签发的令牌任何人都能伪造。
    /// </remarks>
    public string SigningKey { get; set; } = "ledon-development-signing-key-please-change";

    /// <summary>访问令牌有效期，默认 1 小时。</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>API Key 配置（服务间调用/内部系统对接的轻量认证）。</summary>
public sealed class ApiKeyOptions
{
    /// <summary>携带 API Key 的请求头，默认 X-Api-Key。</summary>
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>Key → 持有者名称。为空表示不启用 API Key 认证。</summary>
    public Dictionary<string, string> Keys { get; set; } = [];
}

/// <summary>认证授权总配置。</summary>
public sealed class LedonSecurityOptions
{
    public JwtOptions Jwt { get; set; } = new();

    public ApiKeyOptions ApiKey { get; set; } = new();

    /// <summary>权限声明类型（claim type），默认 permission。</summary>
    public string PermissionClaimType { get; set; } = "permission";

    /// <summary>刷新令牌有效期，默认 7 天（见 <see cref="IRefreshTokenService"/>）。</summary>
    public TimeSpan RefreshTokenExpiration { get; set; } = TimeSpan.FromDays(7);
}
