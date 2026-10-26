using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Quotas;

/// <summary>配额中间件：按 <see cref="QuotaOptions.KeySelector"/> 做固定窗口限流。</summary>
public sealed class QuotaMiddleware(RequestDelegate next, QuotaService quotas, QuotaOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var key = options.KeySelector(context);
        var decision = await quotas.ConsumeAsync(key, options.DefaultLimit, options.Window, context.RequestAborted)
            .ConfigureAwait(false);

        context.Response.Headers["X-Quota-Limit"] = options.DefaultLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-Quota-Remaining"] = decision.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!decision.Allowed)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((decision.ResetAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);

            await context.Response.WriteAsJsonAsync(new
            {
                error = "quota_exceeded",
                message = "请求配额已用尽，请稍后重试",
                retryAfterSeconds = retryAfter,
            }).ConfigureAwait(false);

            return;
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>配额注册与应用扩展。</summary>
public static class LedonQuotaServiceCollectionExtensions
{
    /// <summary>
    /// 注册配额服务（默认进程内；<c>UseDistributedCache = true</c> 且已注册 IDistributedCache 时多实例共享）：
    /// <code>
    /// builder.Services.AddLedonQuotas(o =&gt; o.DefaultLimit = 100);
    /// app.UseLedonQuotas();
    /// </code>
    /// </summary>
    public static IServiceCollection AddLedonQuotas(
        this IServiceCollection services,
        Action<QuotaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new QuotaOptions();
        configure?.Invoke(options);

        if (options.DefaultLimit < 0)
            throw new InvalidOperationException("QuotaOptions.DefaultLimit 不能为负数");

        if (options.Window <= TimeSpan.Zero)
            throw new InvalidOperationException("QuotaOptions.Window 必须大于 0");

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IQuotaStore>(provider =>
        {
            if (options.UseDistributedCache
                && provider.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>() is { } cache)
            {
                return new DistributedQuotaStore(cache, options);
            }

            return new InMemoryQuotaStore(provider.GetRequiredService<TimeProvider>());
        });
        services.TryAddSingleton<QuotaService>();

        return services;
    }

    /// <summary>启用配额中间件（在路由/端点之前）。</summary>
    public static IApplicationBuilder UseLedonQuotas(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<QuotaMiddleware>();
    }
}
