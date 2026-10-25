using Microsoft.Extensions.Logging;

namespace Ledon.BlobStorage;

/// <summary>
/// 本地磁盘存储：单实例部署 / 开发默认。目录按路径段自动创建；
/// 并发写用文件级异步锁串行化，避免同文件半写半读。
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly LedonBlobStorageOptions _options;
    private readonly ILogger<LocalFileStorage> _logger;

    // 路径段 → 该文件的异步锁。键含完整路径，锁粒度到文件
    private readonly Dictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public LocalFileStorage(LedonBlobStorageOptions options, ILogger<LocalFileStorage> logger)
    {
        _options = options;
        _logger = logger;

        var root = ResolveRoot(options.RootPath);
        Directory.CreateDirectory(root);
    }

    public async Task SaveAsync(
        string path, Stream content, BlobUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);
        BlobPath.ValidateExtension(path, _options);

        var full = FullPath(path);
        var gate = GateFor(full);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!(options?.Overwrite ?? false) && File.Exists(full))
                throw new IOException($"文件已存在：{path}（需要覆盖请传 Overwrite = true）");

            var length = await CopyWithLimitAsync(content, full, _options.MaxFileSize, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("已写入 {Path}（{Length} 字节）", path, length);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);

        var full = FullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"文件不存在：{path}", path);

        Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);
        return Task.FromResult(File.Exists(FullPath(path)));
    }

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);

        var full = FullPath(path);
        if (!File.Exists(full))
            return Task.FromResult(false);

        File.Delete(full);
        PruneEmptyDirectories(Path.GetDirectoryName(full)!);
        return Task.FromResult(true);
    }

    public Task<BlobInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        BlobPath.Validate(path);

        var full = FullPath(path);
        if (!File.Exists(full))
            return Task.FromResult<BlobInfo?>(null);

        var info = new FileInfo(full);
        return Task.FromResult<BlobInfo?>(new BlobInfo(
            path,
            info.Length,
            ContentTypes.Guess(path),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
    }

    public Task<IReadOnlyList<BlobInfo>> ListAsync(
        string? prefix = null, int pageSize = 100, string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(_options.RootPath);
        var searchRoot = string.IsNullOrEmpty(prefix) ? root : FullPath(prefix);

        if (!Directory.Exists(searchRoot))
            return Task.FromResult<IReadOnlyList<BlobInfo>>([]);

        var files = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Select(full => new { Full = full, Relative = Path.GetRelativePath(root, full) })
            .Where(item => item.Relative.StartsWith(prefix ?? "", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Skip(ParseSkip(continuationToken))
            .Take(Math.Max(1, pageSize))
            .Select(item =>
            {
                var info = new FileInfo(item.Full);
                return new BlobInfo(
                    item.Relative.Replace('\\', '/'),
                    info.Length,
                    ContentTypes.Guess(item.Relative),
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<BlobInfo>>(files);
    }

    private string FullPath(string path)
    {
        var root = ResolveRoot(_options.RootPath);
        var combined = Path.GetFullPath(Path.Combine(root, BlobPath.Normalize(path)));

        // 防目录穿越：规范化后的路径必须仍在根目录内
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"路径越出存储根目录：{path}", nameof(path));

        var directory = Path.GetDirectoryName(combined);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return combined;
    }

    private static string ResolveRoot(string root) =>
        Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(AppContext.BaseDirectory, root));

    private SemaphoreSlim GateFor(string fullPath)
    {
        lock (_gate)
        {
            if (!_fileLocks.TryGetValue(fullPath, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _fileLocks[fullPath] = gate;
            }

            return gate;
        }
    }

    private static int ParseSkip(string? continuationToken) =>
        int.TryParse(continuationToken, out var skip) ? Math.Max(0, skip) : 0;

    private static void PruneEmptyDirectories(string directory)
    {
        // 只清理空目录，避免删文件后留一层层空壳
        while (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            var parent = Path.GetDirectoryName(directory);
            Directory.Delete(directory);

            if (parent is null || parent == directory)
                break;

            directory = parent;
        }
    }

    private static async Task<long> CopyWithLimitAsync(
        Stream content, string destination, long maxFileSize, CancellationToken cancellationToken)
    {
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;

            if (maxFileSize > 0 && total > maxFileSize)
                throw new IOException($"文件超过大小上限（{maxFileSize} 字节）");

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }
}
