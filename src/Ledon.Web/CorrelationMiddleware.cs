using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ledon.Web;

/// <summary>
/// 关联 ID 中间件：请求没有 <c>X-Correlation-Id</c> 时生成（优先取当前 TraceId，
/// 让关联 ID 与链路 ID 天然对齐）；回写响应头，并压入日志作用域——
/// 同一请求的所有日志自动带上 CorrelationId，跨服务排查时串得起整条链。
/// </summary>
internal sealed class CorrelationMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationMiddleware> _logger;

    public CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            // 有链路时直接用 TraceId：日志关联 ID 与 OTel 链路 ID 一致，排查不用二次映射
            correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString()
                ?? Guid.NewGuid().ToString("n");
        }

        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        System.Diagnostics.Activity.Current?.SetTag("correlation.id", correlationId);

        await _next(context).ConfigureAwait(false);
    }
}
