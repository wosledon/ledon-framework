using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Scalar.AspNetCore;

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

        // 跨域：必须在路由之后、认证授权之前（端点元数据上的 [EnableCors] 才能生效）
        if (options.UseCors)
            app.UseCors(options.CorsPolicyName);

        // 认证授权：检测到任何认证方案（如 Ledon.Security 注册的）时自动接线，
        // 未配置认证时为零开销跳过——不用记"还要加 UseAuthentication"
        if (app.Services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>() is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        foreach (var endpoint in app.Services.GetServices<IEndpoint>())
            endpoint.Map(app);

        if (options.UseControllers)
            app.MapControllers();

        if (options.UseOpenApi)
            app.MapOpenApi(options.OpenApiPath);

        // Scalar API 文档 UI（按需）
        if (options.UseScalarUi)
        {
            app.MapScalarApiReference(options.ScalarPath, scalar =>
                scalar.AddDocument("v1", "Ledon API", options.OpenApiPath));
        }

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
