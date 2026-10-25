using Ledon.BlobStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 文件存储入口（本地 / S3(MinIO) / 内存，业务只依赖 <see cref="IFileStorage"/>）：
/// <code>
/// builder.Services.AddLedonBlobStorage(o =&gt;
/// {
///     o.Provider = LedonBlobProvider.Local;
///     o.RootPath = "App_Data/blobs";
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

        switch (options.Provider)
        {
            case LedonBlobProvider.Local when string.IsNullOrWhiteSpace(options.RootPath):
                throw new InvalidOperationException("使用本地存储需要配置 RootPath");
        }

        services.TryAddSingleton(options);

        // 具体存储（本地/内存）。S3 兼容存储见 Ledon.BlobStorage.S3（按需引入，同样实现 IFileStorage）
        services.TryAddSingleton<IFileStorage>(provider =>
        {
            var resolved = provider.GetRequiredService<LedonBlobStorageOptions>();
            var logger = provider.GetRequiredService<ILoggerFactory>();

            IFileStorage storage = resolved.Provider switch
            {
                LedonBlobProvider.InMemory => new InMemoryFileStorage(resolved),
                _ => new LocalFileStorage(resolved, logger.CreateLogger<LocalFileStorage>()),
            };

            // 多租户部署：套一层租户路径前缀（未启用多租户时直通，无额外行为）
            return options.TenantPathPrefix ? new TenantPrefixedFileStorage(storage, provider) : storage;
        });

        return services;
    }
}
