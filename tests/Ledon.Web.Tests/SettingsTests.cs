using Ledon.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>运行时设置：强类型读写、加密、前缀查询、文件持久化。</summary>
public class SettingsTests
{
    [Fact]
    public async Task Typed_values_roundtrip_and_can_be_removed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSettings();

        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettingsService>();

        await settings.SetAsync("smtp", new SmtpSettings("smtp.example.com", 587));
        await settings.SetAsync("smtp:user", "alice");
        await settings.SetAsync("other", 42);

        var smtp = await settings.GetAsync<SmtpSettings>("smtp");
        Assert.NotNull(smtp);
        Assert.Equal("smtp.example.com", smtp.Host);
        Assert.Equal(587, smtp.Port);

        var all = await settings.GetAllAsync<string>("smtp:");
        Assert.Equal("alice", all["smtp:user"]);

        Assert.True(await settings.RemoveAsync("other"));
        Assert.Null(await settings.GetAsync<int?>("other"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => settings.GetRequiredAsync<SmtpSettings>("missing"));
    }

    [Fact]
    public async Task Encrypted_values_are_not_stored_in_plaintext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSettings();

        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettingsService>();
        var store = provider.GetRequiredService<ISettingsStore>();

        await settings.SetAsync("secret", "super-secret", encrypted: true);

        var entry = await store.GetAsync("secret");
        Assert.NotNull(entry);
        Assert.True(entry.Encrypted);
        Assert.StartsWith("dp:v1:", entry.Value);
        Assert.DoesNotContain("super-secret", entry.Value);

        Assert.Equal("super-secret", await settings.GetAsync<string>("secret"));
    }

    [Fact]
    public async Task Json_file_store_survives_new_provider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ledon-settings-{Guid.NewGuid():N}.json");

        try
        {
            await using (var first = BuildProvider(path))
            {
                var settings = first.GetRequiredService<ISettingsService>();
                await settings.SetAsync("feature", new FeatureSettings(true, "abc"));
            }

            await using (var second = BuildProvider(path))
            {
                var settings = second.GetRequiredService<ISettingsService>();
                var feature = await settings.GetRequiredAsync<FeatureSettings>("feature");

                Assert.True(feature.Enabled);
                Assert.Equal("abc", feature.Name);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Missing_store_path_uses_in_memory_store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSettings();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemorySettingsStore>(provider.GetRequiredService<ISettingsStore>());
    }

    private static ServiceProvider BuildProvider(string path)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonSettings(o => o.StorePath = path);

        return services.BuildServiceProvider();
    }

    public sealed record SmtpSettings(string Host, int Port);

    public sealed record FeatureSettings(bool Enabled, string Name);
}
