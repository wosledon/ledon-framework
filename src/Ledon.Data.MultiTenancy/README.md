# Ledon.Data.MultiTenancy

按租户路由数据库连接（库/schema 物理隔离），复用 `Ledon.MultiTenancy` 的租户解析和 `Ledon.MultiTenancy.Management` 的租户目录。

```csharp
builder.Services.AddLedonMultiTenancy();
builder.Services.AddLedonTenantManagement();
builder.Services.AddLedonTenantData<AppDbContext>(o =>
{
    o.DefaultConnectionString = configuration.GetConnectionString("Default");
    o.ConfigureDbContext = (db, connectionString) => db.UseNpgsql(connectionString);
    o.MigrationStrategy = LedonMigrationStrategy.MigrateOnly;
});
```

解析连接串：

```csharp
public class OrdersService(ITenantDbContextFactory<AppDbContext> factory)
{
    public async Task<List<Order>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateAsync(ct: ct); // 当前租户
        return await db.Orders.ToListAsync(ct);
    }
}
```

迁移编排：

```csharp
var orchestrator = scope.ServiceProvider.GetRequiredService<ITenantMigrationOrchestrator<AppDbContext>>();
var results = await orchestrator.MigrateAllAsync(); // 默认库 + 所有启用租户
```

规则：

- 租户未配置 `ConnectionString` 时回退 `DefaultConnectionString`（单库 + TenantId 过滤模式）；
- 租户停用/不存在/无连接串分别抛出明确异常；
- `ITenantDbContextFactory<T>`、`ITenantMigrationOrchestrator<T>` 是 Scoped，调用方负责释放 DbContext；
- `MigrateAllAsync` 汇总每个租户的结果（成功/失败/挂起数），单个租户失败不会中断其余租户；
- 共享库与租户库都迁移时，`<default>` 会先执行。

完整文档：<https://github.com/wosledon/ledon-framework>
