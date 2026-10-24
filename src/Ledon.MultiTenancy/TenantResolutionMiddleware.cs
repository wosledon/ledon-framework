using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ledon.MultiTenancy;

/// <summary>租户解析中间件：从请求头/查询串解析租户 ID 并放入 HttpContext.Items。</summary>
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
        var tenantId = _options.Source == TenantSource.QueryString
            ? context.Request.Query[_options.QueryStringName].FirstOrDefault()
            : context.Request.Headers[_options.HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // 未带租户标识：单租户模式（所有数据无隔离），记录 Debug 便于排查"为什么没隔离"
            _logger.LogDebug("请求未携带租户标识（{Header}），按单租户处理", _options.HeaderName);
        }
        else
        {
            context.Items[TenantContext.ItemKey] = tenantId;
        }

        await _next(context);
    }
}
