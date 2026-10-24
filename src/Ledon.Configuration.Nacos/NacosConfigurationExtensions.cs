using Microsoft.Extensions.Configuration;

namespace Ledon.Configuration.Nacos;

/// <summary>
/// Nacos 配置中心入口：把 Nacos 作为配置源接入，支持多 DataId 合并与变更热更新。
/// <code>
/// builder.Configuration.AddLedonNacos(o =>
/// {
///     o.ServerAddresses = ["http://localhost:8848"];
///     o.DataIds = ["myapp", "myapp-database"];
/// });
/// </code>
/// Nacos 上的 properties（key=value）与 JSON（嵌套拍平为 a:b:c）均可解析；
/// 之后 <c>AddLedonOptions&lt;T&gt;</c> 照常绑定 Nacos 里的配置节（含启动校验）。
/// </summary>
public static class NacosConfigurationExtensions
{
    public static IConfigurationBuilder AddLedonNacos(
        this IConfigurationBuilder configuration,
        Action<NacosOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new NacosOptions();
        configure?.Invoke(options);

        if (options.ServerAddresses.Length == 0)
            throw new InvalidOperationException("AddLedonNacos: 至少配置一个 ServerAddresses");

        if (options.DataIds.Length == 0)
            throw new InvalidOperationException("AddLedonNacos: 至少配置一个 DataIds");

        configuration.Add(new NacosConfigurationSource(options));
        return configuration;
    }
}
