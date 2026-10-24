# Ledon.Serilog

Ledon Framework 的 Serilog 结构化日志集成：一行接入，自动带 OTel TraceId/SpanId 富化。

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddLedonSerilog();   // 其余框架接入不变
```

- **配置驱动**：`appsettings.json` 里有 `Serilog` 节时按配置装载（sink/级别/富化全在配置里）；没配时退回控制台 sink；
- **自动富化**：`FromLogContext` + 当前 Activity 的 `TraceId`/`SpanId`——日志与链路/span 在后端按同一 TraceId 互跳；
- **可覆盖**：`configure` 回调追加任何 Serilog 配置：

```csharp
builder.AddLedonSerilog(config => config
    .MinimumLevel.Debug()
    .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day));
```

```json
{
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [{ "Name": "Console" }]
  }
}
```

与 `Ledon.Observability` 的 OTLP 日志导出互补：Serilog 管结构化输出与 sink 生态，OTel 管与 trace/metrics 同资源同端点。

完整文档：<https://github.com/wosledon/ledon-framework>
