namespace Ledon.MultiTenancy;

/// <summary>租户解析来源。</summary>
public enum TenantSource
{
    /// <summary>请求头（默认）。</summary>
    Header,

    /// <summary>查询字符串。</summary>
    QueryString,
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
}
