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

    /// <summary>
    /// 缓存键的租户前缀（默认 <c>tenant:</c>）：配了缓存包（AddLedonCaching 等）时，
    /// 当前租户下的缓存键自动变成 <c>tenant:{租户ID}:{原key}</c>——不配则租户间互串（数据泄漏）。
    /// 置空可关闭（自行在 key 里带租户）。
    /// </summary>
    public string CacheKeyPrefix { get; set; } = "tenant:";

    /// <summary>
    /// 是否强制要求租户标识，默认 <c>false</c>。
    /// <para>
    /// 开启后解析不到租户 = 直接 400，不再"按单租户放行"。多租户 SaaS 应该打开——
    /// 否则漏带 <c>X-Tenant-Id</c> 的请求会静默看到全量数据（跨租户泄漏），
    /// 而且这种泄漏在日志里只有一行 Debug，极难发现。
    /// </para>
    /// </summary>
    public bool RequireTenant { get; set; }

    /// <summary>
    /// 解析不到租户时的响应状态码，默认 400。改成 401 可以让未认证请求先走登录。
    /// </summary>
    public int MissingTenantStatusCode { get; set; } = 400;

    /// <summary>
    /// 租户目录校验器（可选）：解析到租户 ID 后校验它是否真实存在/启用。
    /// 不实现则信任请求里的值——客户端可以枚举租户 ID，租户多了等于把 ID 空间敞开。
    /// <code>
    /// builder.Services.AddLedonMultiTenancy(o =&gt; o.TenantValidator = new MyTenantValidator());
    /// </code>
    /// </summary>
    public ITenantValidator? TenantValidator { get; set; }
}

/// <summary>
/// 租户目录校验：解析到的租户 ID 是否真实可用。
/// 实现方通常查自己的租户表（可加缓存）。
/// </summary>
public interface ITenantValidator
{
    /// <summary>校验租户；返回 false 时请求按 <see cref="MultiTenancyOptions.MissingTenantStatusCode"/> 拒绝。</summary>
    ValueTask<bool> IsValidAsync(string tenantId, CancellationToken cancellationToken = default);
}
