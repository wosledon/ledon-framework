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

    // 固定数量的文件锁（按路径哈希取模）：既串行化同文件写入，也不会因文件数增长而无限累积
    private const int LockStripes = 64;
    private readonly SemaphoreSlim[] _fileLocks = Enumerable.Range(0, LockStripes)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

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
        path = BlobPath.Validate(path);
        BlobPath.ValidateExtension(path, _options);

        var full = FullPath(path, createDirectory: true);
        var gate = GateFor(full);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!(options?.Overwrite ?? false) && File.Exists(full))
                throw new IOException($"文件已存在：{path}（需要覆盖请传 Overwrite = true）");

            // 先写临时文件再原子替换：失败（如超大小上限）不会留下半截文件，也不会破坏旧内容
            var temp = $"{full}.tmp-{Guid.NewGuid():N}";

            try
            {
                var length = await CopyWithLimitAsync(
                    content, temp, _options.MaxFileSize, cancellationToken).ConfigureAwait(false);

                File.Move(temp, full, overwrite: options?.Overwrite ?? false);

                _logger.LogDebug("已写入 {Path}（{Length} 字节）", path, length);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = BlobPath.Validate(path);

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
        path = BlobPath.Validate(path);
        return Task.FromResult(File.Exists(FullPath(path)));
    }

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = BlobPath.Validate(path);

        var root = ResolveRoot(_options.RootPath);
        var full = FullPath(path);
        if (!File.Exists(full))
            return Task.FromResult(false);

        File.Delete(full);
        PruneEmptyDirectories(Path.GetDirectoryName(full)!, root);
        return Task.FromResult(true);
    }

    public Task<BlobInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        path = BlobPath.Validate(path);

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

        // prefix 是"键前缀"而不是目录：从根枚举后按相对路径过滤（"logs" 也能命中 "logs-2024/a.txt"）
        var normalizedPrefix = (prefix ?? string.Empty).Replace('\\', '/').Trim('/');
        var searchRoot = root;

        if (!Directory.Exists(searchRoot))
            return Task.FromResult<IReadOnlyList<BlobInfo>>([]);

        var files = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Select(full => new { Full = full, Relative = Path.GetRelativePath(root, full).Replace('\\', '/') })
            .Where(item => item.Relative.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Skip(ParseSkip(continuationToken))
            .Take(Math.Max(1, pageSize))
            .Select(item =>
            {
                var info = new FileInfo(item.Full);
                return new BlobInfo(
                    item.Relative,
                    info.Length,
                    ContentTypes.Guess(item.Relative),
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<BlobInfo>>(files);
    }

    private string FullPath(string path, bool createDirectory = false)
    {
        var root = ResolveRoot(_options.RootPath);
        var combined = Path.GetFullPath(Path.Combine(root, BlobPath.Normalize(path)));

        // 防目录穿越：规范化后的路径必须仍在根目录内
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"路径越出存储根目录：{path}", nameof(path));

        if (createDirectory)
        {
            var directory = Path.GetDirectoryName(combined);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        return combined;
    }

    private static string ResolveRoot(string root) =>
        Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(AppContext.BaseDirectory, root));

    private SemaphoreSlim GateFor(string fullPath) =>
        _fileLocks[(uint)fullPath.GetHashCode(StringComparison.OrdinalIgnoreCase) % LockStripes];

    private static int ParseSkip(string? continuationToken) =>
        int.TryParse(continuationToken, out var skip) ? Math.Max(0, skip) : 0;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响主流程（下次覆盖写会生成新的临时名）
        }
        catch (UnauthorizedAccessException)
        {
            // 同上：权限问题留给调用方从原始异常定位
        }
    }

    private static void PruneEmptyDirectories(string directory, string root)
    {
        // 只清理空目录；到存储根目录即停，绝不越界删除根之外的目录
        while (Directory.Exists(directory)
            && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
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
