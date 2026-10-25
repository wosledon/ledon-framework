using Ledon.AuditLog;
using Microsoft.AspNetCore.Builder;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>审计中间件注册扩展。</summary>
public static class LedonAuditApplicationBuilderExtensions
{
    /// <summary>自动填充操作人/IP（在映射端点之前调用）。</summary>
    public static IApplicationBuilder UseLedonAuditContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<AuditContextMiddleware>();
    }
}
