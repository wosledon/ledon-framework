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

可靠投递的发件箱见 `Ledon.Queues`。

完整文档：<https://github.com/wosledon/ledon-framework>
