namespace Ledon.BlobStorage;

/// <summary>存储选项。</summary>
public sealed class LedonBlobStorageOptions
{
    /// <summary>提供程序，默认 <see cref="LedonBlobProvider.Local"/>。</summary>
    public LedonBlobProvider Provider { get; set; } = LedonBlobProvider.Local;

    /// <summary>本地存储根目录（<see cref="LedonBlobProvider.Local"/> 必填），默认 <c>App_Data/blobs</c>。</summary>
    public string RootPath { get; set; } = Path.Combine("App_Data", "blobs");

    /// <summary>
    /// S3 兼容服务地址（MinIO 填 <c>http://localhost:9000</c>）。
    /// <para>预留：内置提供程序不含 S3；自定义 <c>IFileStorage</c> 可直接读取这些配置。</para>
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>S3 区域（MinIO 可填任意值，如 <c>us-east-1</c>）。预留，供自定义实现读取。</summary>
    public string? Region { get; set; }

    /// <summary>S3 桶名。预留，供自定义实现读取。</summary>
    public string? BucketName { get; set; }

    /// <summary>访问密钥。预留，供自定义实现读取。</summary>
    public string? AccessKey { get; set; }

    /// <summary>密钥。预留，供自定义实现读取。</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// 是否按租户自动加路径前缀（默认 <c>true</c>）。多租户部署下租户间文件天然隔离；
    /// 未启用多租户时无租户上下文，前缀为空，行为与单租户一致。
    /// </summary>
    public bool TenantPathPrefix { get; set; } = true;

    /// <summary>单文件大小上限，默认 100 MB（0 = 不限）。</summary>
    public long MaxFileSize { get; set; } = 100 * 1024 * 1024;

    /// <summary>允许的扩展名（小写带点，如 <c>.png</c>）；空 = 不限制。</summary>
    public ISet<string> AllowedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>存储提供程序。</summary>
public enum LedonBlobProvider
{
    /// <summary>本地磁盘（单实例 / 开发）。</summary>
    Local = 0,

    /// <summary>内存存储（测试用）。</summary>
    InMemory = 1,
}
