# 可观测性

```csharp
builder.Services.AddLedonObservability(o =>
{
    o.ServiceName = "my-app";
    o.OtlpEndpoint = new Uri("http://localhost:4317");   // 或读 OTEL_EXPORTER_OTLP_ENDPOINT
    o.UseConsoleExporter = true;                          // 开发排障：span 直接打控制台
});
```

基于 OpenTelemetry：链路（Tracing）+ 指标（Metrics）+ ASP.NET Core / HttpClient / Runtime 自动插桩。

## 框架自身即可观测

不需要任何额外代码，以下活动自动产出 span：

| Span                       | 时机                         |
| -------------------------- | ---------------------------- |
| `Module.<Name>`            | 模块生命周期各阶段           |
| `Event.<TEvent>`           | 事件发布                     |
| `Queue.<...>`              | 队列任务处理                 |
| `ScheduledTask.<Name>`     | 定时任务执行                 |

单个调用方取消只影响它自己。模块内可发自定义 span：

```csharp
public async Task OnInitializedAsync(ModuleContext ctx, CancellationToken ct = default)
{
    using var span = ctx.StartActivity("加载配置");
    await LoadAsync(ct);
}
```

## 关联 ID

`UseLedonWeb()` 默认开启：`X-Correlation-Id` 贯穿请求头 → 响应头 → 日志作用域 → ProblemDetails（未携带则自动生成，携带则原样回显）。跨服务调用时透传上游 ID 即可串全链。

## 日志导出

`AddLedonObservability()` 默认把 ILogger 日志也经 OTLP 导出（含日志作用域——关联 ID、租户等 scope 值随日志一起到达后端），与链路/指标共用同一端点与资源：

```csharp
builder.Services.AddLedonObservability(o =>
{
    o.EnableLogs = false;   // 不需要日志导出时关掉（默认开）
});
```

三位一体：trace（请求/模块/事件/队列/定时任务）+ metrics（ASP.NET/HttpClient/Runtime）+ logs（含作用域）。

## 健康检查

```csharp
builder.Services.AddLedonHealthChecks();   // IHealthCheck 实现被自动发现

app.MapLedonLiveness();    // /health/live  进程在跑即 200（重启判断）
app.MapLedonReadiness();   // /health/ready 聚合全部检查（摘流判断）
```

实现 `IHealthCheck` 即被自动发现并聚合。存活/就绪分离：就绪探针失败时存活仍为 200，避免编排系统（K8s）误杀进程。

## 接入现有体系

OTLP 端点指向任意兼容后端（Jaeger / Tempo / Grafana Cloud / 阿里云 ARMS……）。开发环境无基础设施时开 `UseConsoleExporter` 直接看 span。
