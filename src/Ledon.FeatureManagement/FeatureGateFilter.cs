using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.FeatureManagement;

/// <summary>
/// 端点开关过滤器：开关未启用时端点返回 404（像不存在一样，不暴露功能存在性）。
/// 用法：app.MapGet("/beta/report", ...).AddEndpointFilter(new FeatureGateFilter("beta-report"))
/// </summary>
public sealed class FeatureGateFilter(string feature) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var manager = context.HttpContext.RequestServices.GetRequiredService<IFeatureManager>();
        var target = FeatureManager.ResolveTarget(context.HttpContext.RequestServices);

        if (!manager.IsEnabled(feature, target))
            return Results.NotFound();

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>端点过滤器用的 <see cref="FeatureGateFilter"/> 特性形式（可选语法糖）。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class FeatureGateAttribute(string feature) : Attribute
{
    /// <summary>开关名。</summary>
    public string Feature { get; } = feature;
}