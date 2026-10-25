using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.FeatureManagement;

/// <summary>
/// 功能开关判定实现：配置（含配置中心热更新）+ 运行时覆盖 + 百分比/白名单灰度。
/// </summary>
public sealed class FeatureManager : IFeatureManager
{
    private readonly ConfigurationFeatureStore _store;
    private readonly LedonFeatureManagementOptions _options;
    private readonly IConfiguration _configuration;

    public FeatureManager(ConfigurationFeatureStore store, LedonFeatureManagementOptions options, IConfiguration configuration)
    {
        _store = store;
        _options = options;
        _configuration = configuration;
    }

    public IReadOnlyCollection<string> Features =>
        _store.Snapshot().Select(definition => definition.Name).ToArray();

    public bool IsEnabled(string feature, FeatureTarget target = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);

        var definition = _store.Get(feature);
        if (definition is null)
            return _options.DefaultEnabled;

        return Evaluate(definition, target);
    }

    public ValueTask<bool> IsEnabledAsync(
        string feature, FeatureTarget target = default, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IsEnabled(feature, target));

    public void SetEnabled(string feature, bool enabled) => _store.SetEnabled(feature, enabled);

    /// <summary>判定顺序：显式开关 → 租户白名单 → 用户白名单 → 百分比灰度。</summary>
    private bool Evaluate(FeatureDefinition definition, FeatureTarget target)
    {
        if (definition.Enabled.HasValue)
            return definition.Enabled.Value;

        if (target.TenantId is { Length: > 0 } tenant
            && definition.Tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase))
            return true;

        if (target.UserId is { Length: > 0 } user
            && definition.Users.Contains(user, StringComparer.OrdinalIgnoreCase))
            return true;

        return _store.InPercentageBucket(target, definition);
    }

    /// <summary>从当前请求解析灰度主体（多租户上下文 + 声明）。未启用对应能力时退化为机器标识。</summary>
    internal static FeatureTarget ResolveTarget(IServiceProvider services)
    {
        var httpContext = services.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()?.HttpContext;

        var tenant = services.GetService<Ledon.MultiTenancy.ITenantContext>()?.TenantId;

        var user = httpContext?.User?.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst("sub")?.Value
              ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;

        return new FeatureTarget(tenant, user, httpContext?.Session?.Id);
    }
}
