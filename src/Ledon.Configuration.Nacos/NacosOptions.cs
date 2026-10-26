namespace Ledon.Configuration.Nacos;

/// <summary>Nacos 配置中心参数。</summary>
public sealed class NacosOptions
{
    /// <summary>Nacos 服务端地址列表（多地址按序故障转移），如 http://localhost:8848。</summary>
    public string[] ServerAddresses { get; set; } = [];

    /// <summary>命名空间（tenant），为空表示 public。</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>配置分组，默认 DEFAULT_GROUP。</summary>
    public string Group { get; set; } = "DEFAULT_GROUP";

    /// <summary>要加载的配置 DataId 列表（多个按序合并，后者覆盖前者同键）。</summary>
    public string[] DataIds { get; set; } = [];

    /// <summary>控制台账号（Nacos 开启鉴权时登录换 accessToken）。</summary>
    public string? UserName { get; set; }

    /// <summary>控制台密码。</summary>
    public string? Password { get; set; }

    /// <summary>直接指定 accessToken（跳过登录）；与 UserName/Password 二选一。</summary>
    public string? AccessToken { get; set; }

    /// <summary>配置变更轮询间隔，默认 30 秒（长轮询不可用时的回退间隔）。</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否使用 Nacos 原生长轮询（<c>/v1/cs/configs/listener</c>），默认 <c>true</c>。</summary>
    public bool UseLongPolling { get; set; } = true;

    /// <summary>长轮询挂起时长，默认 30 秒（Nacos 服务端上限通常也是 30 秒）。</summary>
    public TimeSpan LongPollingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 自定义 HTTP 消息处理器（测试注入假服务器用）；生产无需设置。
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}
