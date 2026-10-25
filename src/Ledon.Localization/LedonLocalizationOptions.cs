using System.Globalization;

namespace Ledon.Localization;

/// <summary>文化解析来源。</summary>
[Flags]
public enum CultureSources
{
    /// <summary>不使用任何来源（只用默认文化）。</summary>
    None = 0,

    /// <summary>查询字符串 <c>?culture=zh-CN</c>。</summary>
    QueryString = 1,

    /// <summary>Cookie。</summary>
    Cookie = 2,

    /// <summary><c>Accept-Language</c> 请求头。</summary>
    Header = 4,

    /// <summary>默认全开。</summary>
    All = QueryString | Cookie | Header,
}

/// <summary>本地化选项。</summary>
public sealed class LedonLocalizationOptions
{
    /// <summary>默认文化，默认 <c>zh-CN</c>。</summary>
    public string DefaultCulture { get; set; } = "zh-CN";

    /// <summary>支持的文化列表（首个为默认兜底）。</summary>
    public ISet<string> SupportedCultures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "zh-CN",
        "en-US",
    };

    /// <summary>文化解析来源，默认 <see cref="CultureSources.All"/>。</summary>
    public CultureSources Sources { get; set; } = CultureSources.All;

    /// <summary>查询参数名，默认 <c>culture</c>。</summary>
    public string QueryStringParameter { get; set; } = "culture";

    /// <summary>Cookie 名，默认 <c>.ledon.culture</c>。</summary>
    public string CookieName { get; set; } = ".ledon.culture";

    /// <summary>请求头回退时是否把选中的文化写回 Cookie（记住用户选择）。</summary>
    public bool RememberChoice { get; set; } = true;

    /// <summary>把当前文化应用到线程当前文化（影响 DateTime.ToString 等本地 API）。</summary>
    public bool ApplyToCurrentCulture { get; set; } = true;

    /// <summary>归一化文化名（大小写/下划线），非法文化返回 <c>null</c>。</summary>
    public string? Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
            return null;

        var normalized = culture.Replace('_', '-').Trim();

        if (!SupportedCultures.Contains(normalized))
            return null;

        try
        {
            // 规范大小写：EN-us → en-US
            return CultureInfo.GetCultureInfo(normalized).Name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>文化解析结果（含来源，便于排查"为什么界面是英文"）。</summary>
public sealed record CultureResolution(string Culture, CultureSources Source)
{
    /// <summary>默认文化（无来源命中）。</summary>
    public static CultureResolution Default(string culture) => new(culture, CultureSources.None);
}

/// <summary>从请求解析文化的策略（可单测，不依赖 HTTP 上下文类型）。</summary>
public interface ICultureResolver
{
    /// <summary>按优先级解析：查询串 → Cookie → Accept-Language → 默认。</summary>
    CultureResolution Resolve(CultureRequest request);
}

/// <summary>请求中的文化线索（与 ASP.NET 解耦的最小形状）。</summary>
public sealed record CultureRequest(
    string? QueryStringValue,
    string? CookieValue,
    string? AcceptLanguage);
