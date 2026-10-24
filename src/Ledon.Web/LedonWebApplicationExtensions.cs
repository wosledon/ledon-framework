using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web;

/// <summary>
/// 请求管线装配：关联 ID → 路由 → 自动发现的端点 → 控制器 → OpenAPI（可选）。
/// </summary>
public static class LedonWebApplicationExtensions
{
    public static WebApplication UseLedonWeb(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<LedonWebOptions>();

        // 关联 ID：默认开启（请求/响应头/日志作用域三位一体）
        if (options.UseCorrelationId)
            app.UseMiddleware<CorrelationMiddleware>();

        // 全局异常处理（ProblemDetails）
        if (options.UseProblemDetails)
            app.UseExceptionHandler();

        app.UseRouting();

        foreach (var endpoint in app.Services.GetServices<IEndpoint>())
            endpoint.Map(app);

        if (options.UseControllers)
            app.MapControllers();

        if (options.UseOpenApi)
            app.MapOpenApi(options.OpenApiPath);

        return app;
    }

    /// <summary>单独启用关联 ID 中间件（未使用 <see cref="UseLedonWeb"/> 时）。</summary>
    public static IApplicationBuilder UseLedonCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationMiddleware>();
        return app;
    }
}
