using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ledon.MultiTenancy;

/// <summary>
/// 租户解析中间件：按 <see cref="MultiTenancyOptions.Source"/> 从请求头/查询串/用户声明/Host 子域名
/// 解析租户 ID 并放入 HttpContext.Items。
/// </summary>
internal sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MultiTenancyOptions _options;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(
        RequestDelegate next,
        MultiTenancyOptions options,
        ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = _options.Source switch
        {
            TenantSource.QueryString => context.Request.Query[_options.QueryStringName].FirstOrDefault(),
            TenantSource.Claim => context.User?.FindFirst(_options.ClaimType)?.Value,
            TenantSource.Host => ResolveFromHost(context.Request.Host.Host),
            _ => context.Request.Headers[_options.HeaderName].FirstOrDefault(),
        };

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // 强制模式：解析不到租户直接拒绝，不按单租户放行（否则漏带头就会看到全量数据）
            if (_options.RequireTenant)
            {
                _logger.LogWarning(
                    "请求缺少租户标识（来源: {Source}），已按 RequireTenant 拒绝", _options.Source);

                context.Response.StatusCode = _options.MissingTenantStatusCode;
                await context.Response.WriteAsJsonAsync(
                    new { error = "missing_tenant", message = "请求缺少租户标识" },
                    context.RequestAborted).ConfigureAwait(false);

                return;
            }

            // 未带租户标识：单租户模式（所有数据无隔离），记录 Debug 便于排查"为什么没隔离"
            _logger.LogDebug(
                "请求未解析到租户标识（来源: {Source}），按单租户处理", _options.Source);
        }
        else
        {
            // 租户目录校验：不信任请求里的值，否则客户端可以枚举租户 ID
            if (_options.TenantValidator is not null
                && !await _options.TenantValidator.IsValidAsync(tenantId, context.RequestAborted)
                    .ConfigureAwait(false))
            {
                _logger.LogWarning("租户未通过目录校验，已拒绝");

                context.Response.StatusCode = _options.MissingTenantStatusCode;
                await context.Response.WriteAsJsonAsync(
                    new { error = "unknown_tenant", message = "未知或已停用的租户" },
                    context.RequestAborted).ConfigureAwait(false);

                return;
            }

            context.Items[TenantContext.ItemKey] = tenantId;
        }

        // 环境租户随请求管线流动：查询过滤（ApplyTenantFilters）等取当前租户的场景依赖它
        using (TenantContext.SetAmbient(tenantId))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    /// <summary>从 Host 取子域名作为租户标识；apex 域名/无关域名返回 null（单租户）。</summary>
    private string? ResolveFromHost(string host)
    {
        if (string.IsNullOrEmpty(host))
            return null;

        if (string.IsNullOrEmpty(_options.HostSuffix))
            return host;

        var suffix = _options.HostSuffix.TrimStart('.');
        if (host.Length <= suffix.Length
            || !host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || host[^(suffix.Length + 1)] != '.')
        {
            return null;
        }

        return host[..^(suffix.Length + 1)];
    }
}
