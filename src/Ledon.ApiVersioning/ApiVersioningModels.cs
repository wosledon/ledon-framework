namespace Ledon.ApiVersioning;

/// <summary>API 版本化选项（基于 Asp.Versioning，Apache-2.0）。</summary>
public sealed class LedonApiVersioningOptions
{
    /// <summary>默认版本，默认 1.0。</summary>
    public string DefaultVersion { get; set; } = "1.0";

    /// <summary>是否支持 URL 段（/v{version}/ 或 api/v{version}/），默认 <c>true</c>。</summary>
    public bool UseUrlSegment { get; set; } = true;

    /// <summary>版本请求头，默认 X-Api-Version。</summary>
    public string HeaderName { get; set; } = "X-Api-Version";

    /// <summary>版本查询参数，默认 api-version。</summary>
    public string QueryParameterName { get; set; } = "api-version";

    /// <summary>媒体类型版本参数，默认 v（application/json;v=2.0）。</summary>
    public string MediaTypeParameterName { get; set; } = "v";

    /// <summary>未指定版本时是否使用默认版本，默认 <c>true</c>。</summary>
    public bool AssumeDefaultVersionWhenUnspecified { get; set; } = true;

    /// <summary>是否在响应中报告支持的版本，默认 <c>true</c>。</summary>
    public bool ReportApiVersions { get; set; } = true;

    /// <summary>弃用标记响应头，默认 Deprecation。</summary>
    public string DeprecationHeaderName { get; set; } = "Deprecation";

    /// <summary>下线时间响应头，默认 Sunset。</summary>
    public string SunsetHeaderName { get; set; } = "Sunset";
}

/// <summary>标记 API/端点已弃用；Sunset 使用 RFC 1123 或 ISO 8601 字符串。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false, Inherited = true)]
public sealed class ApiDeprecatedAttribute(string? sunset = null, string? link = null) : Attribute
{
    public string? Sunset { get; } = sunset;

    /// <summary>弃用说明/迁移文档链接。</summary>
    public string? Link { get; } = link;
}

/// <summary>版本读取器工厂（便于测试/自定义组合）。</summary>
public static class ApiVersionReaders
{
    public static Asp.Versioning.IApiVersionReader Create(LedonApiVersioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var readers = new List<Asp.Versioning.IApiVersionReader>
        {
            new Asp.Versioning.HeaderApiVersionReader(options.HeaderName),
            new Asp.Versioning.QueryStringApiVersionReader(options.QueryParameterName),
            new Asp.Versioning.MediaTypeApiVersionReader(options.MediaTypeParameterName),
        };

        if (options.UseUrlSegment)
            readers.Insert(0, new Asp.Versioning.UrlSegmentApiVersionReader());

        return Asp.Versioning.ApiVersionReader.Combine([.. readers]);
    }
}
