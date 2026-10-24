using Ledon.Caching;

namespace Ledon.MultiTenancy;

/// <summary>
/// 租户缓存键前缀：当前租户下 <c>{CacheKeyPrefix}{租户ID}:</c>，无租户上下文时返回 null
/// （用静态前缀——单租户应用不受影响）。读环境租户（AsyncLocal），单例安全。
/// </summary>
internal sealed class TenantCacheKeyPrefixProvider(MultiTenancyOptions options)
    : ICacheKeyPrefixProvider
{
    public string? GetKeyPrefix()
    {
        var tenantId = TenantContext.AmbientTenantId;
        return string.IsNullOrEmpty(tenantId) ? null : $"{options.CacheKeyPrefix}{tenantId}:";
    }
}
