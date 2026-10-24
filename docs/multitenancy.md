# 多租户

```csharp
builder.Services.AddLedonMultiTenancy();
app.UseLedonMultiTenancy();
```

## 租户解析

默认从 `X-Tenant-Id` 请求头解析（`o.HeaderName` 可改），也支持查询串：

```csharp
builder.Services.AddLedonMultiTenancy(o => o.Source = TenantSource.QueryString);  // ?tenantId=xxx
```

- 未携带租户标识时按**单租户**处理（`TenantId == null`，`IsMultiTenant == false`），记 Debug 日志；
- 解析结果经 `ITenantContext` 暴露给请求管线内的任意服务：

```csharp
app.MapGet("/whoami", (ITenantContext tenant) => new { tenant.TenantId, tenant.IsMultiTenant });
```

## 后台任务的环境租户穿透

Web 请求外的后台工作（队列任务、定时任务）默认拿不到请求上下文的租户。用 `AsyncLocal` 环境租户穿透：

```csharp
// 入队时捕获当前环境租户
await queue.EnqueueAsync(async (scope, token) =>
{
    var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();  // 仍能看到 tenant-a
    await ProcessAsync(tenant.TenantId, token);
});
```

## 数据隔离：约定显式

框架提供**上下文与解析**，不提供隐式查询过滤。每个查询显式带上租户条件：

```csharp
db.Orders.Where(o => o.TenantId == tenant.TenantId);
```

理由：隐式全局过滤会让"跨租户统计"这类需求变得困难，且容易在 join 场景产生意外遗漏。约定显式，隔离责任在数据层清晰可见。

## 边界

解析来源目前只有请求头/查询串。JWT claim、子域名、路由值等解析策略可在 `TenantResolutionMiddleware` 基础上自行扩展（或订阅后续版本）。
