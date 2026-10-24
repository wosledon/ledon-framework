# Ledon.MultiTenancy

Ledon Framework 的多租户：租户解析（请求头 / 查询串 / 用户声明 / Host 子域名）+ 后台任务环境穿透。

```csharp
builder.Services.AddLedonMultiTenancy(o =>
{
    o.Source = TenantSource.Host;      // tenant-a.example.com → tenant-a
    o.HostSuffix = ".example.com";
});

app.UseLedonMultiTenancy();
```

```csharp
app.MapGet("/whoami", (ITenantContext tenant) => new { tenant.TenantId, tenant.IsMultiTenant });
```

未解析到租户标识时按单租户处理（`TenantId == null`）。后台任务经 AsyncLocal 环境租户穿透（入队捕获、执行恢复）。

**数据隔离约定显式**：框架提供上下文与解析，不提供隐式查询过滤——每个查询显式带租户条件（`db.Orders.Where(o => o.TenantId == tenant.TenantId)`），避免隐式过滤的意外。

完整文档：<https://github.com/wosledon/ledon-framework>
