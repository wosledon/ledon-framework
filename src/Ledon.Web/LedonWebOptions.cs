namespace Ledon.Web;

/// <summary>Web 功能开关。默认只开"控制器自动发现"，其余按需开启。</summary>
public sealed class LedonWebOptions
{
    /// <summary>自动发现控制器（默认开）：扫描到的业务程序集会挂为 MVC ApplicationPart。</summary>
    public bool UseControllers { get; set; } = true;

    /// <summary>启用 OpenAPI 文档（默认关，按需开启；基于 .NET 10 内置 AddOpenApi/MapOpenApi）。</summary>
    public bool UseOpenApi { get; set; }

    /// <summary>OpenAPI 文档路径。</summary>
    public string OpenApiPath { get; set; } = "/openapi/v1.json";

    /// <summary>
    /// Scalar API 文档 UI（默认关）：开启后 <c>UseLedonWeb</c> 自动映射
    /// <see cref="ScalarPath"/> 指向 OpenAPI 文档的可视化页面。需同时开启 <see cref="UseOpenApi"/>。
    /// </summary>
    public bool UseScalarUi { get; set; }

    /// <summary>Scalar UI 路径，默认 /scalar。</summary>
    public string ScalarPath { get; set; } = "/scalar";

    /// <summary>
    /// Data Protection 密钥环的 Redis 连接串（默认空 = 不用 Redis）。
    /// 多实例部署配它：cookie/防伪令牌的加密码环共享，任意实例都能解密另一个实例签发的数据
    /// （不配则各实例本地密钥环，负载均衡后会出现间歇性 400/登录态丢失）。
    /// </summary>
    public string? DataProtectionRedisConnectionString { get; set; }

    /// <summary>
    /// Data Protection 应用标识（多实例/多服务共享码环时必须一致），默认取入口程序集名。
    /// </summary>
    public string? DataProtectionApplicationName { get; set; }

    /// <summary>
    /// 幂等中间件（默认关）：带 <c>Idempotency-Key</c> 请求头的变更请求按
    /// (方法+路径+Key) 缓存响应——重放返回缓存、并发同 Key 只执行一次、只缓存 2xx。
    /// 需要 IDistributedCache 在场（<c>AddDistributedMemoryCache</c> 或 <c>AddLedonDistributedCaching</c>）。
    /// </summary>
    public bool UseIdempotency { get; set; }

    /// <summary>幂等响应缓存时长，默认 24 小时。</summary>
    public TimeSpan IdempotencyKeyTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>关联 ID（X-Correlation-Id，贯穿请求头/响应头/日志作用域），默认开。</summary>
    public bool UseCorrelationId { get; set; } = true;

    /// <summary>
    /// 未处理异常转 ProblemDetails（RFC 7807，含关联 ID 与 TraceId），默认开。
    /// 关闭后异常按默认行为冒泡。
    /// </summary>
    public bool UseProblemDetails { get; set; } = true;

    /// <summary>
    /// 统一响应包装：成功响应包一层 <c>{"code":0,"data":...}</c>，默认关。
    /// 纯 REST / 开放 API 场景不建议开启。
    /// </summary>
    public bool UseUnifiedResponse { get; set; }

    /// <summary>
    /// API 版本化（URL 段：<c>api/v{version}/controller</c>），默认关。
    /// 控制器用 <c>[ApiVersion("1.0")]</c> 标注。
    /// </summary>
    public bool UseApiVersioning { get; set; }

    /// <summary>
    /// 跨域（CORS）：默认关。开启后按 <see cref="CorsOrigins"/> 注册名为
    /// <see cref="CorsPolicyName"/> 的默认策略并接入请求管线；
    /// <see cref="CorsOrigins"/> 为空表示允许任意来源（仅建议开发环境）。
    /// 需要自定义策略时自行 <c>AddCors</c> 并改 <see cref="CorsPolicyName"/>。
    /// </summary>
    public bool UseCors { get; set; }

    /// <summary>UseLedonWeb 接入的 CORS 策略名，默认 ledon-default。</summary>
    public string CorsPolicyName { get; set; } = "ledon-default";

    /// <summary>允许的跨域来源（精确匹配协议+主机+端口，如 https://app.example.com）。</summary>
    public string[] CorsOrigins { get; set; } = [];
}
