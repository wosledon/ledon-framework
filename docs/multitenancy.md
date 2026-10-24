# 多租户

```csharp
builder.Services.AddLedonMultiTenancy();
app.UseLedonMultiTenancy();
```

## 租户解析

四种解析来源（`o.Source`）：

| 来源      | 配置                                  | 示例                              |
| --------- | ------------------------------------- | --------------------------------- |
| `Header`（默认） | `o.HeaderName`                 | `X-Tenant-Id: tenant-a`           |
| `QueryString`   | `o.QueryStringName`           | `?tenantId=tenant-a`              |
| `Claim`         | `o.ClaimType`（默认 tenant_id）| 令牌里的租户声明                  |
| `Host`          | `o.HostSuffix`                | `tenant-a.example.com` → tenant-a |

```csharp
// 子域名多租户
builder.Services.AddLedonMultiTenancy(o =>
{
    o.Source = TenantSource.Host;
    o.HostSuffix = ".example.com";
});
```

- `Claim` 来源要求认证先于租户解析：`app.UseAuthentication(); app.UseLedonMultiTenancy();`
- `Host` 来源下 apex 域名（`example.com`）与无关域名按单租户处理
- 任何来源未解析到租户标识时按**单租户**处理（`TenantId == null`，`IsMultiTenant == false`），记 Debug 日志
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

路由值解析（`/tenants/{tenantId}/...`）未内置，可在 `TenantResolutionMiddleware` 基础上自行扩展。后台任务的环境租户靠入队时捕获，**不会**随每次队列派发重新解析——跨租户复用的 worker 进程需显式 `SetAmbient`。