using Ledon.Settings;

namespace Ledon.Web.Tests;

/// <summary>设置存储边界：非法路径、损坏文件容错。</summary>
public class SettingsEdgeTests
{
    [Fact]
    public void Invalid_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileSettingsStore("   "));
    }

    [Fact]
    public async Task Corrupt_file_keeps_store_usable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ledon-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ not-json");

        try
        {
            using var store = new JsonFileSettingsStore(path);

            Assert.Null(await store.GetAsync("missing"));
            await store.SetAsync(new SettingEntry
            {
                Key = "recovered",
                Value = "\"ok\"",
                Encrypted = false,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });

            Assert.NotNull(await store.GetAsync("recovered"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
