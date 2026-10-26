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

    [Fact]
    public void Json_store_reads_and_hot_reloads()
    {
        var directory = CreateTempDirectory();

        try
        {
            var file = Path.Combine(directory, "zh-CN.json");
            File.WriteAllText(file, """{ "Hi": "你好" }""");

            var store = new JsonFileLocalizationStore(directory, ["zh-CN"]);

            Assert.Equal("你好", store.Get("zh-CN", "Hi"));

            File.WriteAllText(file, """{ "Hi": "哈喽", "Bye": "再见" }""");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));

            // 文件变更即时生效（按修改时间 + 长度检测，不缓存旧内容）
            Assert.Equal("哈喽", store.Get("zh-CN", "Hi"));
            Assert.Equal("再见", store.GetAll("zh-CN")["Bye"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Json_store_keeps_last_good_snapshot_for_malformed_file()
    {
        var directory = CreateTempDirectory();

        try
        {
            var file = Path.Combine(directory, "zh-CN.json");
            File.WriteAllText(file, """{ "Hi": "你好" }""");

            var store = new JsonFileLocalizationStore(directory, ["zh-CN"]);
            Assert.Equal("你好", store.Get("zh-CN", "Hi"));

            File.WriteAllText(file, "{ 这不是 JSON");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));

            // 坏文件不把异常冒到请求管线，也不丢掉上一份可用文案
            Assert.Equal("你好", store.Get("zh-CN", "Hi"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Json_store_handles_duplicate_keys_and_deleted_file()
    {
        var directory = CreateTempDirectory();

        try
        {
            var file = Path.Combine(directory, "zh-CN.json");
            File.WriteAllText(file, """{ "Hi": "1", "Hi": "2" }""");

            var store = new JsonFileLocalizationStore(directory, ["zh-CN"]);

            // JSON 允许重复属性名：后者覆盖前者，不抛异常
            Assert.Equal("2", store.Get("zh-CN", "Hi"));

            File.Delete(file);
            Assert.Null(store.Get("zh-CN", "Hi"));
            Assert.Empty(store.GetAll("zh-CN"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Json_store_rejects_path_traversal_in_culture()
    {
        var directory = CreateTempDirectory();

        try
        {
            Assert.Throws<ArgumentException>(() =>
                new JsonFileLocalizationStore(directory, ["../outside"]));

            var store = new JsonFileLocalizationStore(directory, ["zh-CN"]);

            Assert.Throws<ArgumentException>(() => store.Get("../../secret", "Key"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ledon-loc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
