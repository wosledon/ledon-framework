using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web;

/// <summary>
/// 请求管线装配：路由 → 自动发现的端点 → 控制器 → OpenAPI（可选）。
/// </summary>
public static class LedonWebApplicationExtensions
{
    public static WebApplication UseLedonWeb(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<LedonWebOptions>();

        app.UseRouting();

        foreach (var endpoint in app.Services.GetServices<IEndpoint>())
            endpoint.Map(app);

        if (options.UseControllers)
            app.MapControllers();

        if (options.UseOpenApi)
            app.MapOpenApi(options.OpenApiPath);

        return app;
    }
}
