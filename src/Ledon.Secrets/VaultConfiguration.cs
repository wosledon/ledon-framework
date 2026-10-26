using Microsoft.Extensions.Configuration;

namespace Ledon.Secrets;

/// <summary>Vault → IConfiguration 配置源（启动时加载，不进 DI 容器）。</summary>
public sealed class VaultConfigurationSource : IConfigurationSource
{
    public VaultConfigurationSource(VaultOptions options, Func<VaultOptions, ISecretProvider>? providerFactory = null)
    {
        Options = options;
        ProviderFactory = providerFactory;
    }

    public VaultOptions Options { get; }

    /// <summary>测试/自定义传输用；为空时创建真实 Vault HTTP 提供器。</summary>
    public Func<VaultOptions, ISecretProvider>? ProviderFactory { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        var provider = ProviderFactory?.Invoke(Options) ?? new VaultSecretProvider(Options);

        return new VaultConfigurationProvider(Options, provider);
    }
}

/// <summary>Vault 配置提供器：加载 Paths 下的字段为配置键。</summary>
public sealed class VaultConfigurationProvider(VaultOptions options, ISecretProvider provider) : ConfigurationProvider
{
    public override void Load()
    {
        try
        {
            foreach (var path in options.Paths)
            {
                var secrets = provider.GetSecretsAsync(path, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                foreach (var (field, value) in secrets)
                    Data[BuildKey(path, field)] = value;
            }
        }
        catch when (options.Optional)
        {
            // 启动容错：Vault 暂时不可用时保持空配置，由应用后续自行兜底
        }
    }

    private string BuildKey(string path, string field)
    {
        var key = options.IncludePathInKey ? $"{path}:{field}" : field;

        return string.IsNullOrEmpty(options.KeyPrefix) ? key : options.KeyPrefix + key;
    }
}

/// <summary>Vault 配置扩展。</summary>
public static class VaultConfigurationExtensions
{
    /// <summary>
    /// 从 Vault 加载配置：
    /// <code>
    /// builder.Configuration.AddLedonVault(o =&gt;
    /// {
    ///     o.Address = "https://vault:8200";
    ///     o.Token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
    ///     o.Paths.Add("myapp");
    ///     o.KeyPrefix = "Vault:";
    /// });
    /// </code>
    /// </summary>
    public static IConfigurationBuilder AddLedonVault(
        this IConfigurationBuilder builder,
        Action<VaultOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new VaultOptions();
        configure?.Invoke(options);

        builder.Add(new VaultConfigurationSource(options));

        return builder;
    }
}
