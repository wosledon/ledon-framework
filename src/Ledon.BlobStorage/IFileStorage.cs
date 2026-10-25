namespace Ledon.BlobStorage;

/// <summary>存储中的对象元数据。</summary>
public sealed record BlobInfo(
    string Path,
    long Length,
    string? ContentType,
    DateTimeOffset LastModifiedUtc);

/// <summary>写入选项。</summary>
public sealed class BlobUploadOptions
{
    /// <summary>内容类型（缺省由扩展名推断）。</summary>
    public string? ContentType { get; init; }

    /// <summary>附加元数据（随对象持久化，列表时可取回）。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>覆盖已存在对象，默认 <c>false</c>（同名抛 <see cref="IOException"/>）。</summary>
    public bool Overwrite { get; init; }
}

/// <summary>
/// 文件/对象存储抽象。业务只依赖接口，本地磁盘 / S3(MinIO) / 内存（测试）可替换：
/// <code>
/// public class AvatarService(IFileStorage storage)
/// {
///     public async Task UploadAsync(string fileName, Stream content, CancellationToken ct)
///         =&gt; await storage.SaveAsync($"avatars/{fileName}", content, ct: ct);
/// }
/// </code>
/// 路径即主键：删、取、列目录都按路径。多租户部署建议配 <see cref="LedonBlobStorageOptions.TenantPathPrefix"/>
/// （默认自动加租户前缀，租户间互相看不到对方文件）。
/// </summary>
public interface IFileStorage
{
    /// <summary>保存（不存在则创建；<see cref="BlobUploadOptions.Overwrite"/> 控制覆盖）。</summary>
    Task SaveAsync(string path, Stream content, BlobUploadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>打开读取流（调用方负责释放）。不存在抛 <see cref="FileNotFoundException"/>。</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>读取为字节数组（小文件方便用）。</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>存在性判断。</summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>删除（不存在返回 <c>false</c>）。</summary>
    Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>取元信息（不存在返回 <c>null</c>）。</summary>
    Task<BlobInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>列出指定前缀下的对象（分页；<paramref name="prefix"/> 为空列全部）。</summary>
    Task<IReadOnlyList<BlobInfo>> ListAsync(string? prefix = null, int pageSize = 100, string? continuationToken = null, CancellationToken cancellationToken = default);
}
