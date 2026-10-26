using Ledon.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 密钥入口：
/// <code>
/// builder.Services.AddLedonSecrets(o =&gt;
/// {
///     o.Address = "https://vault:8200";
///     o.Token = configuration["Vault:Token"];
/// });
/// </code>
/// </summary>
public static class LedonSecretServiceCollectionExtensions
{
    /// <summary>注册 Vault 密钥提供器（带 TTL 缓存）。</summary>
    public static IServiceCollection AddLedonSecrets(
        this IServiceCollection services,
        Action<VaultOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new VaultOptions();
        configure?.Invoke(options);

        if (options.CacheDuration < TimeSpan.Zero)
            throw new InvalidOperationException("VaultOptions.CacheDuration 不能为负数");

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(VaultSecretProvider.HttpClientName);
        services.TryAddSingleton<VaultSecretProvider>(provider =>
            new VaultSecretProvider(options, provider.GetRequiredService<IHttpClientFactory>()));
        services.TryAddSingleton<ISecretProvider>(provider =>
        {
            var vault = provider.GetRequiredService<VaultSecretProvider>();

            return options.CacheDuration > TimeSpan.Zero
                ? new CachingSecretProvider(
                    vault,
                    provider.GetRequiredService<TimeProvider>(),
                    options)
                : vault;
        });

        return services;
    }

    /// <summary>注册内存密钥提供器（开发/测试）。</summary>
    public static IServiceCollection AddLedonInMemorySecrets(
        this IServiceCollection services,
        IEnumerable<KeyValuePair<string, string>>? initial = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISecretProvider>(new InMemorySecretProvider(initial));

        return services;
    }
}
