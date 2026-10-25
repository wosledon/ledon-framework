using System.Diagnostics;
using Ledon.Module.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ledon.Application;

/// <summary>
/// 日志行为：记录请求类型、耗时、结果（失败记警告含异常）。
/// span 名统一 <c>Ledon.Command/{Request}</c>，与链路追踪对齐。
/// </summary>
public sealed class LoggingBehavior<TRequest>(ILogger<LoggingBehavior<TRequest>> logger) : IPipelineBehavior<TRequest>
    where TRequest : notnull
{
    public async ValueTask<object?> HandleAsync(
        TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken = default)
    {
        var name = typeof(TRequest).Name;
        var started = Stopwatch.GetTimestamp();

        using var activity = LedonActivity.Modules.StartActivity($"Ledon.Command/{name}");
        activity?.SetTag("ledon.request", name);

        try
        {
            var response = await next().ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            logger.LogInformation("请求 {Request} 完成（{ElapsedMs} ms）", name, elapsed.TotalMilliseconds.ToString("0"));
            activity?.SetTag("ledon.duration_ms", elapsed.TotalMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "请求 {Request} 失败", name);
            throw;
        }
    }
}
