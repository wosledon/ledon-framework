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

### 需要全局过滤时：ApplyTenantFilters

实体实现 `IMultiTenant`（`TenantId` 属性），并在 `OnModelCreating` 里**显式**启用过滤（与软删除过滤同一风格）：

```csharp
public class Order : IMultiTenant
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // 每请求/每作用域设置当前租户（Web 中间件解析后写入；后台任务 SetAmbient 穿透后写入）
    public string? CurrentTenantId { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyTenantFilters(this, context => context.CurrentTenantId);
}
```

- 过滤表达式引用**上下文实例成员**（EF 文档的查询过滤模式），EF 每次查询按当前上下文实例重新求值；
- **不要**传闭包捕获 DbContext 字段的 `Func`——EF 按上下文类型缓存模型与编译后的查询计划，Func 结果会被首个查询固化；
- 无租户上下文时默认不过滤（宿主/后台穿透视角可见全部行，单租户应用不受影响）；`filterWhenNoTenant: true` 则改为过滤为空（严格隔离）。

## 边界

路由值解析（`/tenants/{tenantId}/...`）未内置，可在 `TenantResolutionMiddleware` 基础上自行扩展。后台任务的环境租户靠入队时捕获，**不会**随每次队列派发重新解析——跨租户复用的 worker 进程需显式 `SetAmbient`。