namespace Ledon.MultiTenancy;

/// <summary>租户解析来源。</summary>
public enum TenantSource
{
    /// <summary>请求头（默认）。</summary>
    Header,

    /// <summary>查询字符串。</summary>
    QueryString,

    /// <summary>
    /// 已认证用户的声明（claim 类型见 <see cref="MultiTenancyOptions.ClaimType"/>）。
    /// 需将 UseLedonMultiTenancy() 置于认证中间件之后。
    /// </summary>
    Claim,

    /// <summary>
    /// 请求 Host 的子域名（后缀见 <see cref="MultiTenancyOptions.HostSuffix"/>），
    /// 如 tenant-a.example.com → tenant-a。
    /// </summary>
    Host,
}

/// <summary>多租户默认参数。</summary>
public sealed class MultiTenancyOptions
{
    /// <summary>请求头名，默认 X-Tenant-Id。</summary>
    public string HeaderName { get; set; } = "X-Tenant-Id";

    /// <summary>查询参数名（<see cref="TenantSource.QueryString"/> 时用），默认 tenantId。</summary>
    public string QueryStringName { get; set; } = "tenantId";

    /// <summary>解析来源，默认请求头。</summary>
    public TenantSource Source { get; set; } = TenantSource.Header;

    /// <summary>声明类型（<see cref="TenantSource.Claim"/> 时用），默认 tenant_id。</summary>
    public string ClaimType { get; set; } = "tenant_id";

    /// <summary>
    /// 域名后缀（<see cref="TenantSource.Host"/> 时用，如 .example.com）；为空表示整个 Host 即租户标识。
    /// Host 不以后缀结尾（apex 域名或无关域名）时按单租户处理。
    /// </summary>
    public string HostSuffix { get; set; } = string.Empty;
}
