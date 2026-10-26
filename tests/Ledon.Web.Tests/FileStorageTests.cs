using Ledon.BlobStorage;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>文件存储：本地/内存实现共用的契约（路径、覆盖、大小、穿越、列表）。</summary>
public abstract class FileStorageContract
{
    protected abstract IFileStorage CreateStorage(LedonBlobStorageOptions options);

    protected virtual LedonBlobStorageOptions Options() => new()
    {
        Provider = LedonBlobProvider.InMemory,
        MaxFileSize = 1024,
    };

    private IFileStorage Storage() => CreateStorage(Options());

    [Fact]
    public async Task Save_then_read_roundtrip()
    {
        var storage = Storage();

        await storage.SaveAsync("docs/a.txt", new MemoryStream("hello"u8.ToArray()));

        Assert.True(await storage.ExistsAsync("docs/a.txt"));
        Assert.Equal("hello", await ReadTextAsync(storage, "docs/a.txt"));
    }

    [Fact]
    public async Task Save_refuses_overwrite_by_default()
    {
        var storage = Storage();
        await storage.SaveAsync("a.txt", new MemoryStream("v1"u8.ToArray()));

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveAsync("a.txt", new MemoryStream("v2"u8.ToArray())));
    }

    [Fact]
    public async Task Overwrite_replaces_content()
    {
        var storage = Storage();
        await storage.SaveAsync("a.txt", new MemoryStream("v1"u8.ToArray()));

        await storage.SaveAsync("a.txt", new MemoryStream("v2"u8.ToArray()), new BlobUploadOptions { Overwrite = true });

        Assert.Equal("v2", await ReadTextAsync(storage, "a.txt"));
    }

    [Fact]
    public async Task Missing_file_throws_and_delete_reports_false()
    {
        var storage = Storage();

        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync("nope.txt"));
        Assert.False(await storage.DeleteAsync("nope.txt"));
    }

    [Fact]
    public async Task Size_limit_is_enforced()
    {
        var storage = Storage();

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveAsync("big.bin", new MemoryStream(new byte[4096])));
    }

    [Fact]
    public async Task Failed_size_limit_leaves_no_partial_file()
    {
        var storage = Storage();

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveAsync("big.bin", new MemoryStream(new byte[4096])));

        // 超限失败必须原子回滚：不能留下半截文件让后续上传撞“已存在”
        Assert.False(await storage.ExistsAsync("big.bin"));
    }

    [Fact]
    public async Task Failed_overwrite_keeps_previous_content()
    {
        var storage = Storage();
        await storage.SaveAsync("a.bin", new MemoryStream(new byte[10]));

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveAsync("a.bin", new MemoryStream(new byte[4096]), new BlobUploadOptions { Overwrite = true }));

        var info = await storage.GetInfoAsync("a.bin");
        Assert.NotNull(info);
        Assert.Equal(10, info!.Length);
    }

    [Fact]
    public async Task Backslash_paths_are_normalized()
    {
        var storage = Storage();

        await storage.SaveAsync("docs\\a.txt", new MemoryStream("hello"u8.ToArray()));

        Assert.True(await storage.ExistsAsync("docs/a.txt"));
    }

    [Fact]
    public async Task List_matches_key_prefix_not_only_directory()
    {
        var storage = Storage();
        await storage.SaveAsync("logs-2024/a.txt", new MemoryStream("a"u8.ToArray()));
        await storage.SaveAsync("other/b.txt", new MemoryStream("b"u8.ToArray()));

        var listed = await storage.ListAsync("logs");

        Assert.Single(listed);
        Assert.Equal("logs-2024/a.txt", listed[0].Path);
    }

    [Fact]
    public async Task Extension_whitelist_is_enforced()
    {
        var options = Options();
        options.AllowedExtensions.Add(".png");
        var storage = CreateStorage(options);

        await storage.SaveAsync("ok.png", new MemoryStream([1, 2, 3]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.SaveAsync("bad.exe", new MemoryStream([1])));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("./")]
    public async Task Traversal_paths_are_rejected(string path)
    {
        var storage = Storage();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SaveAsync(path, new MemoryStream("x"u8.ToArray())));
    }

    [Fact]
    public async Task List_returns_prefix_matches_in_order()
    {
        var storage = Storage();
        await storage.SaveAsync("logs/2.txt", new MemoryStream("b"u8.ToArray()));
        await storage.SaveAsync("logs/1.txt", new MemoryStream("a"u8.ToArray()));
        await storage.SaveAsync("other/1.txt", new MemoryStream("c"u8.ToArray()));

        var listed = await storage.ListAsync("logs");

        Assert.Equal(2, listed.Count);
        Assert.Equal("logs/1.txt", listed[0].Path);
        Assert.Equal("logs/2.txt", listed[1].Path);
        Assert.Equal("text/plain", listed[0].ContentType);
    }

    [Fact]
    public async Task Info_reports_length_and_content_type()
    {
        var storage = Storage();
        await storage.SaveAsync("a.json", new MemoryStream("{}"u8.ToArray()));

        var info = await storage.GetInfoAsync("a.json");

        Assert.NotNull(info);
        Assert.Equal(2, info!.Length);
        Assert.Equal("application/json", info.ContentType);
    }

    private static async Task<string> ReadTextAsync(IFileStorage storage, string path)
    {
        var bytes = await storage.ReadAllBytesAsync(path);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>内存存储实现上述契约。</summary>
public class InMemoryFileStorageTests : FileStorageContract
{
    protected override IFileStorage CreateStorage(LedonBlobStorageOptions options) => new InMemoryFileStorage(options);
}

/// <summary>本地磁盘存储实现同一契约（含目录穿越的真实文件系统防护）。</summary>
public class LocalFileStorageTests : FileStorageContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ledon-blob-tests", Guid.NewGuid().ToString("N"));

    protected override LedonBlobStorageOptions Options() => new()
    {
        Provider = LedonBlobProvider.Local,
        RootPath = _root,
        MaxFileSize = 1024,
    };

    protected override IFileStorage CreateStorage(LedonBlobStorageOptions options) =>
        new LocalFileStorage(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalFileStorage>.Instance);
}
