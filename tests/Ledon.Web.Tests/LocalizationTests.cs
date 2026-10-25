using Ledon.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>文化解析：优先级、q 值、语言回退、非法值。</summary>
public class LocalizationTests
{
    private static DefaultCultureResolver Build(Action<LedonLocalizationOptions>? configure = null)
    {
        var options = new LedonLocalizationOptions();
        configure?.Invoke(options);
        return new DefaultCultureResolver(options);
    }

    [Fact]
    public void Query_string_wins()
    {
        var resolver = Build();

        var resolution = resolver.Resolve(new CultureRequest("en-US", "zh-CN", "zh-CN,zh;q=0.9"));

        Assert.Equal("en-US", resolution.Culture);
        Assert.Equal(CultureSources.QueryString, resolution.Source);
    }

    [Fact]
    public void Cookie_used_when_no_query_string()
    {
        var resolver = Build();

        var resolution = resolver.Resolve(new CultureRequest(null, "en-US", "zh-CN"));

        Assert.Equal("en-US", resolution.Culture);
        Assert.Equal(CultureSources.Cookie, resolution.Source);
    }

    [Fact]
    public void Header_used_when_no_query_or_cookie()
    {
        var resolver = Build();

        var resolution = resolver.Resolve(new CultureRequest(null, null, "en-US,en;q=0.8"));

        Assert.Equal("en-US", resolution.Culture);
        Assert.Equal(CultureSources.Header, resolution.Source);
    }

    [Fact]
    public void Default_culture_when_nothing_matches()
    {
        var resolver = Build();

        var resolution = resolver.Resolve(new CultureRequest(null, null, "fr-FR"));

        Assert.Equal("zh-CN", resolution.Culture);
        Assert.Equal(CultureSources.None, resolution.Source);
    }

    [Fact]
    public void Unsupported_culture_is_ignored_not_crashed()
    {
        var resolver = Build();

        var resolution = resolver.Resolve(new CultureRequest("xx-XX", null, null));

        Assert.Equal("zh-CN", resolution.Culture);
    }

    [Fact]
    public void Accept_language_honors_quality_order()
    {
        var resolver = Build();

        // en 质量高于 zh：应选 en-US
        var resolution = resolver.Resolve(new CultureRequest(null, null, "zh-CN;q=0.5, en-US;q=0.9"));

        Assert.Equal("en-US", resolution.Culture);
    }

    [Fact]
    public void Same_language_other_region_falls_back()
    {
        var resolver = Build();

        // zh-TW 不在支持列表，回退到同语言的 zh-CN
        var resolution = resolver.Resolve(new CultureRequest(null, null, "zh-TW"));

        Assert.Equal("zh-CN", resolution.Culture);
    }

    [Fact]
    public void Sources_can_be_restricted()
    {
        var resolver = Build(o => o.Sources = CultureSources.QueryString);

        var resolution = resolver.Resolve(new CultureRequest(null, "en-US", "en-US"));

        Assert.Equal("zh-CN", resolution.Culture);
    }

    [Fact]
    public void Culture_name_is_normalized()
    {
        var resolver = Build();

        var resolution = resolver.Resolve(new CultureRequest("EN-us", null, null));

        Assert.Equal("en-US", resolution.Culture);
    }

    [Fact]
    public void Default_culture_must_be_supported()
    {
        var services = new ServiceCollection().AddLedonLocalization(o => o.DefaultCulture = "ja-JP");

        // 未登记 ja-JP 时自动加入支持列表，解析不会落到未知文化
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<LedonLocalizationOptions>();

        Assert.Contains("ja-JP", options.SupportedCultures);
    }

    [Fact]
    public void InMemory_store_returns_entries()
    {
        var store = new InMemoryLocalizationStore()
            .Add("zh-CN", new Dictionary<string, string> { ["Hi"] = "你好" })
            .Add("en-US", new Dictionary<string, string> { ["Hi"] = "Hello" });

        Assert.Equal("你好", store.Get("zh-CN", "Hi"));
        Assert.Equal("Hello", store.Get("en-US", "Hi"));
        Assert.Null(store.Get("zh-CN", "Missing"));
        Assert.Equal(["en-US", "zh-CN"], store.Cultures.Order());
    }
}
