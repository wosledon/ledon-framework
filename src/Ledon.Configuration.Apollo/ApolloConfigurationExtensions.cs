using Microsoft.Extensions.Configuration;

namespace Ledon.Configuration.Apollo;

/// <summary>
/// Apollo 配置中心入口：把 Apollo 作为配置源接入，支持多命名空间合并与变更热更新。
/// <code>
/// builder.Configuration.AddLedonApollo(o =>
/// {
///     o.ConfigServer = "http://localhost:8080";
///     o.AppId = "myapp";
/// });
/// </code>
/// application 命名空间的键直接进配置树；其它命名空间以 <c>{命名空间}:content</c> 暴露原始内容。
/// 之后 <c>AddLedonOptions&lt;T&gt;</c> 照常绑定（含启动校验）。
/// </summary>
public static class ApolloConfigurationExtensions
{
    public static IConfigurationBuilder AddLedonApollo(
        this IConfigurationBuilder configuration,
        Action<ApolloOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ApolloOptions();
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.AppId))
            throw new InvalidOperationException("AddLedonApollo: 必须配置 AppId");

        if (string.IsNullOrWhiteSpace(options.ConfigServer)
            || !Uri.TryCreate(options.ConfigServer, UriKind.Absolute, out _))
            throw new InvalidOperationException("AddLedonApollo: ConfigServer 必须是绝对地址（如 http://localhost:8080）");

        if (options.Namespaces.Length == 0)
            throw new InvalidOperationException("AddLedonApollo: 至少配置一个 Namespace");

        if (options.Namespaces.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("AddLedonApollo: Namespace 不能为空");

        if (options.PollingInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("AddLedonApollo: PollingInterval 必须大于 0");

        if (options.LongPollingTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("AddLedonApollo: LongPollingTimeout 必须大于 0");

        configuration.Add(new ApolloConfigurationSource(options));
        return configuration;
    }
}
