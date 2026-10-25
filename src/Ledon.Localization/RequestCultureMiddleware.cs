using System.Globalization;
using Ledon.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 请求文化中间件：解析文化 → 设置 <see cref="CultureInfo.CurrentCulture"/>（含 UI 文化）
/// → 可选把选择写回 Cookie。要在路由/端点之前执行（文化影响日期/数字/文案）。
/// </summary>
public sealed class RequestCultureMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LedonLocalizationOptions _options;

    public RequestCultureMiddleware(RequestDelegate next, LedonLocalizationOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context, ICultureResolver resolver, ILogger<RequestCultureMiddleware> logger)
    {
        var resolution = resolver.Resolve(new CultureRequest(
            context.Request.Query[_options.QueryStringParameter],
            context.Request.Cookies[_options.CookieName],
            context.Request.Headers.AcceptLanguage));

        var culture = CultureInfo.GetCultureInfo(resolution.Culture);

        if (_options.ApplyToCurrentCulture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        // 记住选择：仅当来自请求头且尚未写过 Cookie 时（避免每次请求都 Set-Cookie）
        if (_options.RememberChoice
            && resolution.Source == CultureSources.Header
            && context.Request.Cookies[_options.CookieName] != resolution.Culture)
        {
            context.Response.Cookies.Append(_options.CookieName, resolution.Culture, new CookieOptions
            {
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
            });
        }

        logger.LogDebug("请求文化：{Culture}（来源：{Source}）", resolution.Culture, resolution.Source);

        context.Response.Headers.ContentLanguage = resolution.Culture;

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>本地化中间件注册扩展。</summary>
public static class RequestLocalizationApplicationBuilderExtensions
{
    /// <summary>启用请求文化解析（在 <c>app.UseRouting</c> 之前调用）。</summary>
    public static IApplicationBuilder UseLedonRequestLocalization(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<RequestCultureMiddleware>();
    }
}
