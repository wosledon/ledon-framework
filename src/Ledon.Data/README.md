# Ledon.Data

Ledon Framework 的数据访问：EF Core `DbContext` 自动注册 + 启动前置自动建库/迁移。

```csharp
public class TodoDbContext : DbContext, ILedonDbContext
{
    public DbSet<TodoItem> Todos => Set<TodoItem>();
}
```

```csharp
builder.Services.AddLedonData();                          // 自动注册 + 自动建库/迁移
builder.Services.AddLedonData(o => o.AutoMigrate = false); // 自己管理 schema 时
```

实现 `ILedonDbContext` 即被自动注册，并在启动时**先于任何模块初始化**自动建库（有挂起迁移走 `Migrate`，否则 `EnsureCreated`）。

- **领域事件**：实体收集的事件在 `SaveChanges` 成功后自动分发；注册发件箱后改为同事务落库、可靠投递。
- **审计字段 / 软删除**：实现约定接口即可自动填充创建/修改时间与操作人；删除转为标记删除，查询默认过滤。
- **查询辅助**：内置 `Specification<T>`、分页与动态排序扩展，避免在业务层重复拼 `Where/OrderBy`。
- **通用仓储**：`EfRepository<TContext, TEntity>` 可选使用，复杂查询仍可直接用 `DbContext`。
- **连接弹性**：`AddLedonData` 可配置瞬时故障重试，与 `Ledon.Resilience` 的 HTTP 策略互补。
- **健康检查**：自动登记数据库连通性检查，配合 `Ledon.Observability` 的就绪探针使用。

可靠投递的发件箱见 `Ledon.Queues`。

完整文档：<https://github.com/wosledon/ledon-framework>
