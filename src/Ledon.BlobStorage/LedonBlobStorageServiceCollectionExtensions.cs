using Ledon.BlobStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 文件存储入口（内置本地 / 内存，业务只依赖 <see cref="IFileStorage"/>）；
/// S3/MinIO 等对象存储需自行实现 <see cref="IFileStorage"/> 后替换注册。
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

        if (!Enum.IsDefined(options.Provider))
            throw new InvalidOperationException($"未定义的存储提供程序：{options.Provider}");

        if (options.MaxFileSize < 0)
            throw new InvalidOperationException("LedonBlobStorageOptions.MaxFileSize 不能为负数（0 = 不限）");

        switch (options.Provider)
        {
            case LedonBlobProvider.Local when string.IsNullOrWhiteSpace(options.RootPath):
                throw new InvalidOperationException("使用本地存储需要配置 RootPath");
        }

        services.TryAddSingleton(options);

        // 内置提供程序只有本地/内存。S3 兼容存储需自行实现 IFileStorage 并替换本注册
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
