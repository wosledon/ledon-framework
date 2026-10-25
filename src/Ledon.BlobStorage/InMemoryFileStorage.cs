using System.Collections.Concurrent;
using System.Text;

namespace Ledon.BlobStorage;

/// <summary>
/// 内存存储：测试与单机演示用。进程重启即失，不用于生产持久化。
/// </summary>
public sealed class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, Entry> _blobs = new(StringComparer.Ordinal);
    private readonly LedonBlobStorageOptions _options;

    public InMemoryFileStorage(LedonBlobStorageOptions options) => _options = options;

    public Task SaveAsync(
        string path, Stream content, BlobUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);
        BlobPath.ValidateExtension(path, _options);

        if (!(options?.Overwrite ?? false) && _blobs.ContainsKey(path))
            throw new IOException($"文件已存在：{path}（需要覆盖请传 Overwrite = true）");

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        if (_options.MaxFileSize > 0 && buffer.Length > _options.MaxFileSize)
            throw new IOException($"文件超过大小上限（{_options.MaxFileSize} 字节）");

        _blobs[path] = new Entry(
            buffer.ToArray(),
            options?.ContentType ?? ContentTypes.Guess(path),
            DateTimeOffset.UtcNow,
            options?.Metadata);

        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);

        if (!_blobs.TryGetValue(path, out var entry))
            throw new FileNotFoundException($"文件不存在：{path}", path);

        return Task.FromResult<Stream>(new MemoryStream(entry.Content));
    }

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);

        if (!_blobs.TryGetValue(path, out var entry))
            throw new FileNotFoundException($"文件不存在：{path}", path);

        return Task.FromResult(entry.Content);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);
        return Task.FromResult(_blobs.ContainsKey(path));
    }

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);
        return Task.FromResult(_blobs.TryRemove(path, out _));
    }

    public Task<BlobInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);

        if (!_blobs.TryGetValue(path, out var entry))
            return Task.FromResult<BlobInfo?>(null);

        return Task.FromResult<BlobInfo?>(new BlobInfo(path, entry.Content.Length, entry.ContentType, entry.ModifiedUtc));
    }

    public Task<IReadOnlyList<BlobInfo>> ListAsync(
        string? prefix = null, int pageSize = 100, string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        var skip = int.TryParse(continuationToken, out var parsed) ? Math.Max(0, parsed) : 0;

        var items = _blobs
            .Where(pair => prefix is null || pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Skip(skip)
            .Take(Math.Max(1, pageSize))
            .Select(pair => new BlobInfo(pair.Key, pair.Value.Content.Length, pair.Value.ContentType, pair.Value.ModifiedUtc))
            .ToArray();

        return Task.FromResult<IReadOnlyList<BlobInfo>>(items);
    }

    private sealed record Entry(byte[] Content, string ContentType, DateTimeOffset ModifiedUtc, IReadOnlyDictionary<string, string>? Metadata);
}
