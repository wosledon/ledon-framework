namespace Ledon.OpenIddict;

/// <summary>OpenIddict 客户端定义（配置驱动，替代动态注册）。</summary>
public sealed class OpenIddictClientDefinition
{
    /// <summary>客户端 ID。</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>客户端密钥（空 = 公共客户端，如 SPA/移动端；非空 = 机密客户端）。</summary>
    public string? ClientSecret { get; set; }

    /// <summary>展示名（授权页/日志用）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>允许的授权类型：password / refresh_token / client_credentials。</summary>
    public string[] GrantTypes { get; set; } = [];

    /// <summary>client_credentials 授权时签发的权限（写入令牌 permission claim）。</summary>
    public string[] Permissions { get; set; } = [];

    /// <summary>允许申请的 scope（默认 openid + profile）。</summary>
    public string[] Scopes { get; set; } = ["openid", "profile"];
}

/// <summary>OpenIddict scope 定义。</summary>
public sealed class OpenIddictScopeDefinition
{
    /// <summary>scope 名（如 openid、profile、api）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>展示名。</summary>
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>OpenIddict 存储类型。</summary>
public enum OpenIddictStorage
{
    /// <summary>进程内存储（单实例/开发）。</summary>
    InMemory = 0,

    /// <summary>EF Core 共享数据库（多实例；需注册 <see cref="LedonOpenIddictDbContext"/>）。</summary>
    EntityFrameworkCore = 1,
}

/// <summary>OpenIddict 服务器参数。</summary>
public sealed class LedonOpenIddictOptions
{
    /// <summary>签发者（Issuer，写进令牌与发现文档），默认 https://localhost。</summary>
    public string Issuer { get; set; } = "https://localhost";

    /// <summary>存储类型，默认进程内；多实例改用 <see cref="OpenIddictStorage.EntityFrameworkCore"/>。</summary>
    public OpenIddictStorage Storage { get; set; } = OpenIddictStorage.InMemory;

    /// <summary>
    /// EF 存储时是否在启动阶段 <c>EnsureCreated</c>（默认 <c>false</c>）。
    /// 开发/测试可开；生产建议用 EF 迁移管理表结构。
    /// </summary>
    public bool EnsureDatabaseCreated { get; set; }

    /// <summary>访问令牌有效期，默认 1 小时。</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>刷新令牌有效期，默认 14 天。</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// 签名/加密证书（PFX）路径。多实例部署必须配置同一张证书，否则各实例签发的令牌互不认。
    /// 为空时使用进程内临时密钥（单实例/开发）。
    /// </summary>
    public string? SigningCertificatePath { get; set; }

    /// <summary>PFX 证书密码（无密码留空）。</summary>
    public string? SigningCertificatePassword { get; set; }

    /// <summary>是否映射 /connect/userinfo（默认开；用 [Authorize] 保护，按 access token 返回声明）。</summary>
    public bool UseUserInfoEndpoint { get; set; } = true;

    /// <summary>客户端定义（至少一个；password/client_credentials 均按此校验）。</summary>
    public List<OpenIddictClientDefinition> Clients { get; } = [];

    /// <summary>scope 定义（默认 openid + profile）。</summary>
    public List<OpenIddictScopeDefinition> Scopes { get; } = [];
}
