using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Ledon.Web.ApiConventions;

/// <summary>
/// 全局异常处理：把未处理异常转为 RFC 7807 ProblemDetails（带关联 ID 与 TraceId），
/// 不泄露内部堆栈。经 <c>AddExceptionHandler</c> 注册，由 <c>UseExceptionHandler</c> 启用。
/// </summary>
internal sealed class LedonExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<LedonExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "未处理异常：{Message}", exception.Message);

        var correlationId = httpContext.Items.TryGetValue(CorrelationMiddleware.HeaderName, out var id)
            ? id as string
            : null;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "服务器内部错误",
            Detail = exception.Message,
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
        };

        problem.Extensions["correlationId"] = correlationId ?? httpContext.TraceIdentifier;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString();

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }
}
