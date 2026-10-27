using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Ledon.Web.ApiConventions;

/// <summary>
/// 自定义异常 → HTTP 响应映射。业务包（如 <c>Ledon.Application</c>）不必引用
/// <c>Ledon.Web</c>：把映射实现放在使用方，用
/// <c>AddLedonExceptionMapping&lt;T&gt;()</c> 登记即可，全局异常处理器会自动咨询它。
/// </summary>
public interface IExceptionMapping
{
    /// <summary>尝试映射；返回 <c>false</c> 表示"不归我管"，交给下一个映射或内置规则。</summary>
    bool TryMap(Exception exception, out int statusCode, out string title, out string? detail);
}

/// <summary>
/// 全局异常处理：把未处理异常转为 RFC 7807 ProblemDetails（带关联 ID 与 TraceId），
/// 不泄露内部堆栈。经 <c>AddExceptionHandler</c> 注册，由 <c>UseExceptionHandler</c> 启用。
/// <para>
/// 状态码按异常类型映射，**不再一律 500**：
/// <list type="bullet">
///   <item>自定义映射（<see cref="IExceptionMapping"/>）优先；</item>
///   <item><see cref="ArgumentException"/> → 400；<see cref="UnauthorizedAccessException"/> → 403；</item>
///   <item><see cref="KeyNotFoundException"/> / <see cref="FileNotFoundException"/> → 404；</item>
///   <item><see cref="OperationCanceledException"/> → 499（客户端已断开，不是服务器错误）；</item>
///   <item>其余 → 500，且 <c>Detail</c> 只给通用文案——异常消息可能含 SQL 片段、
///         连接串、文件路径，原样返给客户端等于把内部结构送出去。</item>
/// </list>
/// 真实原因始终写日志（按 Error 级别），调用方拿 correlationId/traceId 来对。
/// </para>
/// </summary>
internal sealed class LedonExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IEnumerable<IExceptionMapping> mappings,
    ILogger<LedonExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // 客户端主动断开：不是错误，也没有人能收到响应体
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("请求被客户端中止");

            return true;
        }

        logger.LogError(exception, "未处理异常：{Message}", exception.Message);

        var correlationId = httpContext.Items.TryGetValue(CorrelationMiddleware.HeaderName, out var id)
            ? id as string
            : null;

        var (status, title, detail) = Map(exception);

        // 4xx 是调用方的问题，不是服务器故障：按警告记，避免真实错误被噪声埋掉
        logger.Log(status is >= 400 and < 500 ? LogLevel.Warning : LogLevel.Error,
            exception, "请求失败：{Status} {Title}", status, title);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
        };

        // 带 Errors 属性的异常（校验类）把逐条原因带回：调用方要能知道改哪里
        if (exception.GetType().GetProperty("Errors")?.GetValue(exception) is System.Collections.IEnumerable errors)
        {
            var list = new List<string>();
            foreach (var error in errors)
                list.Add(error?.ToString() ?? "");

            if (list.Count > 0)
                problem.Extensions["errors"] = list;
        }

        problem.Extensions["correlationId"] = correlationId ?? httpContext.TraceIdentifier;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString();

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    private (int Status, string Title, string Detail) Map(Exception exception)
    {
        foreach (var mapping in mappings)
        {
            if (mapping.TryMap(exception, out var status, out var title, out var detail))
                return (status, title, detail ?? exception.Message);
        }

        var builtIn = StatusCodeFor(exception);

        // 4xx 可以带原因（那是调用方能改的）；5xx 只给通用文案，细节只进日志
        return builtIn is >= 400 and < 500
            ? (builtIn, TitleFor(exception), exception.Message)
            : (builtIn, TitleFor(exception),
                "服务器处理请求时发生错误，请稍后重试或携带 correlationId 联系管理员。");
    }

    private static int StatusCodeFor(Exception exception) => exception switch
    {
        ArgumentException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        FileNotFoundException => StatusCodes.Status404NotFound,
        OperationCanceledException => 499,   // 客户端关闭连接（nginx 约定）
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(Exception exception) => exception switch
    {
        ArgumentException => "请求参数不合法",
        UnauthorizedAccessException => "没有权限",
        KeyNotFoundException => "资源不存在",
        FileNotFoundException => "资源不存在",
        OperationCanceledException => "请求已取消",
        _ => "服务器内部错误",
    };
}
