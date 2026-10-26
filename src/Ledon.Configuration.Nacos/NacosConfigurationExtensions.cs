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

        if (options.ServerAddresses.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("AddLedonNacos: ServerAddresses 不能为空");

        if (options.DataIds.Length == 0)
            throw new InvalidOperationException("AddLedonNacos: 至少配置一个 DataIds");

        if (options.DataIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("AddLedonNacos: DataIds 不能为空");

        if (string.IsNullOrWhiteSpace(options.Group))
            throw new InvalidOperationException("AddLedonNacos: Group 不能为空");

        if (options.PollingInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("AddLedonNacos: PollingInterval 必须大于 0");

        if (options.LongPollingTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("AddLedonNacos: LongPollingTimeout 必须大于 0");

        // 用户名/密码必须成对出现（只配一个会在登录时发空凭据）
        if (string.IsNullOrEmpty(options.UserName) != string.IsNullOrEmpty(options.Password))
            throw new InvalidOperationException("AddLedonNacos: UserName 与 Password 必须同时配置");

        configuration.Add(new NacosConfigurationSource(options));
        return configuration;
    }
}
