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
    /// 为空时使用 AWS S3（按 <see cref="Region"/> 拼区域端点）。
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>S3 区域（MinIO 可填任意值，如 <c>us-east-1</c>）。</summary>
    public string? Region { get; set; } = "us-east-1";

    /// <summary>S3 桶名（S3 提供程序必填）。</summary>
    public string? BucketName { get; set; }

    /// <summary>访问密钥；与 <see cref="SecretKey"/> 同时为空时走 AWS 默认凭证链（环境变量/实例角色）。</summary>
    public string? AccessKey { get; set; }

    /// <summary>密钥。</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>是否使用路径风格地址（MinIO 必须为 <c>true</c>），默认 <c>true</c>。</summary>
    public bool ForcePathStyle { get; set; } = true;

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

    /// <summary>S3 兼容对象存储（AWS S3 / MinIO / 兼容实现）。</summary>
    S3 = 2,
}
