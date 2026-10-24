# Ledon.Aspire

Ledon Framework 的云原生编排默认值：把可观测性、健康检查、弹性、服务发现按 Aspire 约定一次装齐（替代 Aspire 模板的 ServiceDefaults 样板）。

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddLedonServiceDefaults();   // 一行替代 ServiceDefaults.cs

var app = builder.Build();
app.MapLedonDefaultEndpoints();      // /health/live 与 /health/ready
```

包含：

| 能力         | 来源                              | 说明                                   |
| ------------ | --------------------------------- | -------------------------------------- |
| 可观测性     | `AddLedonObservability()`         | OTLP 端点走 `OTEL_EXPORTER_OTLP_ENDPOINT` |
| 健康检查     | `AddLedonHealthChecks()`          | 存活/就绪分离                          |
| HTTP 弹性    | `AddLedonResilience()`            | 重试/超时/熔断接管全部 HttpClient       |
| 服务发现     | `AddServiceDiscovery()`           | `https+服务名://` 寻址，配合 Aspire 编排 |

各项仍是独立能力——需要微调（服务名、Prometheus、Redis 检查……）时直接调对应包的 `AddLedonXxx(o => ...)` 覆盖，或完全不用本包自行组装。

完整文档：<https://github.com/wosledon/ledon-framework>
