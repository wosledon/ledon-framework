# Ledon.Observability

Ledon Framework 的可观测性：OpenTelemetry 链路/指标 + 健康检查，框架自身 span 开箱即用。

```csharp
builder.Services.AddLedonObservability(o =>
{
    o.ServiceName = "my-app";
    o.OtlpEndpoint = new Uri("http://localhost:4317");   // 或读 OTEL_EXPORTER_OTLP_ENDPOINT
    o.UseConsoleExporter = true;                          // 开发排障：span 直接打控制台
});

builder.Services.AddLedonHealthChecks();   // IHealthCheck 实现自动发现并聚合
app.MapLedonLiveness();    // /health/live  进程在跑即 200
app.MapLedonReadiness();   // /health/ready 聚合全部检查
```

模块生命周期、事件发布、队列处理、定时任务执行全部自动产出 span；模块内可发自定义 span：`using var span = ctx.StartActivity("加载配置");`。

日志（ILogger）默认也经 OTLP 导出，含日志作用域（关联 ID 等）——trace/metrics/logs 三位一体；`o.EnableLogs = false` 可关。

`o.UsePrometheusExporter = true` + `app.MapLedonPrometheus()` 暴露 /metrics（Prometheus 拉取）。

完整文档：<https://github.com/wosledon/ledon-framework>
