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
