using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.ApiVersioning;

/// <summary>为标记 <see cref="ApiDeprecatedAttribute"/> 的端点添加 Deprecation/Sunset/Link 响应头。</summary>
public sealed class ApiDeprecationMiddleware(RequestDelegate next, LedonApiVersioningOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<ApiDeprecatedAttribute>();

        if (metadata is not null)
        {
            context.Response.Headers[options.DeprecationHeaderName] = "true";

            if (TryParseDate(metadata.Sunset, out var sunset))
                context.Response.Headers[options.SunsetHeaderName] = sunset.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);

            if (!string.IsNullOrWhiteSpace(metadata.Link))
                context.Response.Headers.Link = $"<{metadata.Link}>; rel=\"deprecation\"";
        }

        await next(context).ConfigureAwait(false);
    }

    internal static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        date = default;

        return !string.IsNullOrWhiteSpace(value)
            && (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)
                || DateTimeOffset.TryParseExact(
                    value,
                    "R",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out date));
    }
}

/// <summary>版本化注册与管线扩展。</summary>
public static class LedonApiVersioningServiceCollectionExtensions
{
    public static IServiceCollection AddLedonApiVersioning(
        this IServiceCollection services,
        Action<LedonApiVersioningOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonApiVersioningOptions();
        configure?.Invoke(options);

        if (!Asp.Versioning.ApiVersionParser.Default.TryParse(options.DefaultVersion, out var defaultVersion))
            throw new InvalidOperationException($"DefaultVersion 非法：{options.DefaultVersion}");

        services.TryAddSingleton(options);

        services.AddApiVersioning(versioning =>
        {
            versioning.DefaultApiVersion = defaultVersion;
            versioning.AssumeDefaultVersionWhenUnspecified = options.AssumeDefaultVersionWhenUnspecified;
            versioning.ReportApiVersions = options.ReportApiVersions;
            versioning.ApiVersionReader = ApiVersionReaders.Create(options);
        });

        return services;
    }

    /// <summary>启用弃用响应头（在 UseRouting 之后、端点之前）。</summary>
    public static IApplicationBuilder UseLedonApiVersioning(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ApiDeprecationMiddleware>();
    }
}
