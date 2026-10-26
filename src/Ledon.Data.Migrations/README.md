# Ledon.Data.Migrations

按模块编排 EF Core 迁移与状态查询（区别于 `Ledon.Data.MultiTenancy` 的按租户库迁移）。

```csharp
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
builder.Services.AddLedonMigration<AppDbContext>("app", order: 1);
builder.Services.AddLedonMigration<OrdersDbContext>("orders", order: 2);
```

```csharp
var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();

var status = await runner.GetStatusAsync();       // 每个模块的 applied/pending
var report = await runner.MigrateAllAsync();      // 逐模块执行，失败不阻断其余模块
// 或只跑一个：await runner.MigrateAsync("orders");
```

- `EfModuleMigration<TContext>` 优先 `MigrateAsync`；没有迁移时可用 `ensureCreated: true` 走 `EnsureCreated`；
- 状态包含数据库是否可连、已应用/挂起迁移与错误信息；
- `MigrationRunner` 按 `Order` 再按名称排序执行，单模块失败写入报告，后续模块继续；
- 部署流水线建议 `Ledon.Data` 设置 `MigrationStrategy = None`，由本模块/CI 统一执行；
- 多实例并发迁移请配合 `Ledon.Locking`（数据库迁移锁）或由部署任务单点执行。

完整文档：<https://github.com/wosledon/ledon-framework>
