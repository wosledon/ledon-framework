namespace Ledon.Configuration.Apollo;

/// <summary>Apollo 配置中心参数。</summary>
public sealed class ApolloOptions
{
    /// <summary>Apollo 配置服务地址（Config Service），如 http://localhost:8080。</summary>
    public string ConfigServer { get; set; } = "http://localhost:8080";

    /// <summary>应用 ID（AppId， Apollo 控制台创建的应用）。</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>集群名，默认 default。</summary>
    public string Cluster { get; set; } = "default";

    /// <summary>命名空间列表，默认 ["application"]（多命名空间按序合并，后者覆盖同键）。</summary>
    public string[] Namespaces { get; set; } = ["application"];

    /// <summary>配置变更轮询间隔，默认 60 秒（Apollo 长轮询的简化替代，配置变更不频繁场景足够）。</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>自定义 HTTP 消息处理器（测试注入假服务端用）；生产无需设置。</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}
