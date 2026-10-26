# Ledon.ApiVersioning

API 版本治理：URL/Header/Query/媒体类型多来源版本、默认版本、响应版本报告与弃用头。

```csharp
builder.Services.AddLedonApiVersioning(o =>
{
    o.DefaultVersion = "1.0";
    o.UseUrlSegment = true;
    o.HeaderName = "X-Api-Version";
    o.QueryParameterName = "api-version";
});

var app = builder.Build();
app.UseRouting();
app.UseLedonApiVersioning();   // 添加 Deprecation/Sunset/Link
```

控制器/端点：

```csharp
[ApiVersion("1.0")]
[ApiVersion("2.0")]
[ApiDeprecated(sunset: "2026-01-01", link: "https://docs.example.com/migrate-v2")]
[ApiController]
[Route("api/v{version:apiVersion}/orders")]
public sealed class OrdersController : ControllerBase { }
```

- URL 段、`X-Api-Version`、`?api-version=`、`application/json;v=2.0` 均可解析；
- `AssumeDefaultVersionWhenUnspecified` 默认开启，响应带 `api-supported-versions`；
- `[ApiDeprecated]` 自动输出 `Deprecation: true`、`Sunset` 与迁移文档 `Link`；
- 基于 `Asp.Versioning`（Apache-2.0），不引入付费组件；
- 与 `LedonWebOptions.UseApiVersioning` 二选一，避免重复配置版本读取器。

完整文档：<https://github.com/wosledon/ledon-framework>
