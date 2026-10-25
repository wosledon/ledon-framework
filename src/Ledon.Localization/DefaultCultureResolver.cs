using System.Globalization;

namespace Ledon.Localization;

/// <summary>默认文化解析：查询串 → Cookie → Accept-Language → 默认文化。</summary>
public sealed class DefaultCultureResolver : ICultureResolver
{
    private readonly LedonLocalizationOptions _options;

    public DefaultCultureResolver(LedonLocalizationOptions options) => _options = options;

    public CultureResolution Resolve(CultureRequest request)
    {
        if (_options.Sources.HasFlag(CultureSources.QueryString))
        {
            var fromQuery = _options.Normalize(request.QueryStringValue);
            if (fromQuery is not null)
                return new CultureResolution(fromQuery, CultureSources.QueryString);
        }

        if (_options.Sources.HasFlag(CultureSources.Cookie))
        {
            var fromCookie = _options.Normalize(request.CookieValue);
            if (fromCookie is not null)
                return new CultureResolution(fromCookie, CultureSources.Cookie);
        }

        if (_options.Sources.HasFlag(CultureSources.Header))
        {
            var fromHeader = ParseAcceptLanguage(request.AcceptLanguage);
            if (fromHeader is not null)
                return new CultureResolution(fromHeader, CultureSources.Header);
        }

        return CultureResolution.Default(_options.DefaultCulture);
    }

    /// <summary>按 q 值降序取第一个受支持的文化（<c>zh-CN,zh;q=0.9,en;q=0.8</c>）。</summary>
    private string? ParseAcceptLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return null;

        var candidates = acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(';', StringSplitOptions.TrimEntries))
            .Select(parts => (Culture: parts[0], Quality: ParseQuality(parts)))
            .OrderByDescending(candidate => candidate.Quality);

        foreach (var (culture, _) in candidates)
        {
            var normalized = _options.Normalize(culture);

            // 语言标签回退：zh-TW 不在支持列表时用 zh-CN 兜底（同语言不同地区）
            normalized ??= FallbackByLanguage(culture);

            if (normalized is not null)
                return normalized;
        }

        return null;
    }

    private string? FallbackByLanguage(string culture)
    {
        var language = culture.Split('-', StringSplitOptions.TrimEntries)[0];

        return _options.SupportedCultures
            .Select(supported => _options.Normalize(supported))
            .FirstOrDefault(supported => supported is not null
                && supported.Split('-', StringSplitOptions.TrimEntries)[0]
                    .Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    private static double ParseQuality(string[] parts)
    {
        foreach (var part in parts.Skip(1))
        {
            if (part.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(part[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var quality))
                return quality;
        }

        return 1.0;
    }
}
