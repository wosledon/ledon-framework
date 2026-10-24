# Ledon.Web

Ledon Framework 的 Web 集成：端点/控制器自动发现、OpenAPI、API 规范化、关联 ID、CORS。

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddLedonWeb(o => o.UseOpenApi = true);   // 模块 + 端点/控制器 + OpenAPI
var app = builder.Build();
app.UseLedonWeb();                                // 管线装配
app.Run();
```

实现 `IEndpoint` 即被自动映射；控制器所在程序集自动挂为 MVC ApplicationPart。

规范化开关（均有默认值）：`UseProblemDetails`（异常 → RFC 7807）、模型验证统一 400、`UseCorrelationId`（X-Correlation-Id 贯穿请求/响应/日志）、`UseUnifiedResponse`（信封结构）、`UseApiVersioning`（URL 段版本化）、`UseCors`（按来源白名单放行）。

完整文档：<https://github.com/wosledon/ledon-framework>
