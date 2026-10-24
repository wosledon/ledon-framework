# Web 开发

## 接入

```csharp
builder.AddLedonWeb(o => o.UseOpenApi = true);   // Web 开关
app.UseLedonWeb();                               // 管线装配
```

## 端点

```csharp
public class HelloEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder) =>
        builder.MapGet("/hello", () => "Hello, Ledon!");
}
```

## 控制器

```csharp
[ApiController]
[Route("api/todos")]
public class TodosController(TodoDbContext db) : ControllerBase { /* ... */ }
```

所在程序集自动挂为 MVC ApplicationPart。

## API 规范化开关

| 开关                 | 默认 | 行为                                          |
| -------------------- | ---- | --------------------------------------------- |
| `UseProblemDetails`  | ✅    | 未处理异常 → RFC 7807（含关联 ID/TraceId）    |
| 模型验证             | ✅    | 失败统一 400 ValidationProblemDetails         |
| `UseCorrelationId`   | ✅    | `X-Correlation-Id` 贯穿请求/响应头/日志作用域 |
| `UseUnifiedResponse` | ❌    | 成功响应包 `{"code":0,"data":...}` 信封       |
| `UseApiVersioning`   | ❌    | URL 段 `api/v{version}/...`                   |
| `UseCors`            | ❌    | 按 `CorsOrigins` 白名单放行跨域               |
| `UseScalarUi`        | ❌    | Scalar 文档 UI（需 `UseOpenApi`）             |

## 跨域（CORS）

```csharp
builder.AddLedonWeb(o =>
{
    o.UseCors = true;
    o.CorsOrigins = ["https://app.example.com"];   // 空数组 = 允许任意来源（仅建议开发环境）
});
```

管线顺序：路由 → CORS → 认证授权 → 端点。预检（OPTIONS）自动应答。

## 多实例：Data Protection 码环

cookie/防伪令牌的加密码环持久化到 Redis（不配则各实例本地码环，负载均衡后间歇 400/登录态丢失）：

```csharp
builder.AddLedonWeb(o => o.DataProtectionRedisConnectionString = "localhost:6379");
```

惰性连接 + AbortOnConnectFail=false：Redis 短暂不在线不阻塞启动。

## 幂等请求

带 `Idempotency-Key` 请求头的变更请求（POST/PUT/PATCH）自动幂等——重放返回缓存响应、并发同 Key 只执行一次（需 `IDistributedCache` 在场）：

```csharp
builder.AddLedonWeb(o => o.UseIdempotency = true);
builder.Services.AddLedonDistributedCaching(o => o.RedisConnectionString = "localhost:6379");
```

- 缓存键 = (方法 + 路径 + Key)，响应带 `Idempotency-Replayed: true` 头表示重放；
- 只缓存 2xx——失败不占键，客户端可带同 Key 重试；
- 进程内单飞；跨实例去重靠共享 `IDistributedCache`（Redis 时多实例生效）。

## API 文档 UI

OpenAPI 只有 JSON 文档；要可视化调试界面开 Scalar UI：

```csharp
builder.AddLedonWeb(o =>
{
    o.UseOpenApi = true;
    o.UseScalarUi = true;   // 打开 /scalar（自动指向 OpenAPI 文档）
});
```

## 认证授权

```csharp
builder.Services.AddLedonSecurity(o => o.Jwt.SigningKey = Configuration["Jwt:Key"]!);
// [Authorize]                        → JWT 或 API Key 任一通过
// [Authorize(Policy = "users.read")]  → 权限策略
```

## 可观测性

```csharp
builder.Services.AddLedonObservability(o => o.ServiceName = "my-app");
builder.Services.AddLedonHealthChecks();
app.MapLedonLiveness(); app.MapLedonReadiness();
```
