using Ledon.BlobStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 文件存储入口（内置本地 / 内存 / S3 兼容，业务只依赖 <see cref="IFileStorage"/>）：
/// <code>
/// builder.Services.AddLedonBlobStorage(o =&gt;
/// {
///     o.Provider = LedonBlobProvider.S3;
///     o.ServiceUrl = "http://localhost:9000";   // MinIO；AWS S3 留空
///     o.BucketName = "uploads";
///     o.AccessKey = "...";
///     o.SecretKey = "...";
/// });
/// </code>
/// </summary>
public static class LedonBlobStorageServiceCollectionExtensions
{
    /// <summary>注册文件存储。</summary>
    public static IServiceCollection AddLedonBlobStorage(this IServiceCollection services)
        => services.AddLedonBlobStorage(_ => { });

    /// <summary>注册文件存储并配置。</summary>
    public static IServiceCollection AddLedonBlobStorage(
        this IServiceCollection services,
        Action<LedonBlobStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonBlobStorageOptions();
        configure(options);

        if (!Enum.IsDefined(options.Provider))
            throw new InvalidOperationException($"未定义的存储提供程序：{options.Provider}");

        if (options.MaxFileSize < 0)
            throw new InvalidOperationException("LedonBlobStorageOptions.MaxFileSize 不能为负数（0 = 不限）");

        switch (options.Provider)
        {
            case LedonBlobProvider.Local when string.IsNullOrWhiteSpace(options.RootPath):
                throw new InvalidOperationException("使用本地存储需要配置 RootPath");

            case LedonBlobProvider.S3 when string.IsNullOrWhiteSpace(options.BucketName):
                throw new InvalidOperationException("使用 S3 存储需要配置 BucketName");

            case LedonBlobProvider.S3 when string.IsNullOrEmpty(options.AccessKey) != string.IsNullOrEmpty(options.SecretKey):
                throw new InvalidOperationException("S3 的 AccessKey 与 SecretKey 必须同时配置（或同时留空走默认凭证链）");

            case LedonBlobProvider.S3 when string.IsNullOrWhiteSpace(options.Region):
                throw new InvalidOperationException("S3 需要配置 Region（MinIO 可填 us-east-1）");
        }

        services.TryAddSingleton(options);

        // 内置提供程序：本地 / 内存 / S3 兼容
        services.TryAddSingleton<IFileStorage>(provider =>
        {
            var resolved = provider.GetRequiredService<LedonBlobStorageOptions>();
            var logger = provider.GetRequiredService<ILoggerFactory>();

            IFileStorage storage = resolved.Provider switch
            {
                LedonBlobProvider.InMemory => new InMemoryFileStorage(resolved),
                LedonBlobProvider.S3 => new S3FileStorage(resolved, logger.CreateLogger<S3FileStorage>()),
                _ => new LocalFileStorage(resolved, logger.CreateLogger<LocalFileStorage>()),
            };

            // 多租户部署：套一层租户路径前缀（未启用多租户时直通，无额外行为）
            return options.TenantPathPrefix ? new TenantPrefixedFileStorage(storage, provider) : storage;
        });

        return services;
    }
}
