# 数据与发件箱

## EF Core

```csharp
public class TodoDbContext : DbContext, ILedonDbContext { /* ... */ }
```

实现 `ILedonDbContext` 即被自动注册，并在启动时（先于所有模块初始化）自动建库/迁移——有挂起迁移走 `Migrate`，否则 `EnsureCreated`。

```csharp
builder.Services.AddLedonData(o => o.AutoMigrate = false);   // 自己管理 schema 时关闭
```

## 审计字段与软删除

实现标记接口即约定生效（`AddLedonData(o => o.EnableAuditing = true)` 后自动填充时间戳/操作人）：

```csharp
public class Order : IAudited          // CreatedAt/CreatedBy/ModifiedAt/ModifiedBy 自动填充
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
}

public class Customer : IAudited, ISoftDelete   // 删除操作自动转换为 IsDeleted = true 更新
{
    public bool IsDeleted { get; set; }
    // ...
}
```

- 操作人来源：注册 `IAuditUserAccessor` 实现（Web 场景用 `Ledon.Security` 的 `ICurrentUser` 适配）；不注册则只填时间戳；
- 软删除转换默认开启（`AutoSoftDelete`），行不出库；
- **查询过滤需显式启用**（与多租户一致的显式边界，避免"查出来的行数悄悄变少"）：

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    modelBuilder.ApplySoftDeleteFilters();   // ISoftDelete 实体自动加 !IsDeleted 过滤
```

- DbContext 发现尊重 `[DisableAutoDiscovery]`（测试夹具/仅手动注册的上下文不参与自动发现）。

## 连接弹性

```csharp
builder.Services.AddLedonData(o => o.EnableRetryOnFailure = true);   // 瞬断重试（默认 3 次）
```

自动定位数据库提供程序的 `EnableRetryOnFailure` 扩展（SqlServer/Npgsql 等）；提供程序没有该扩展时静默跳过（如 SQLite 不支持瞬断重试）。也可在自行配置 DbContext 时手动调用 `optionsBuilder.EnableRetryOnFailure()`。

## 发件箱（Outbox）

跨进程/要求可靠投递的集成事件，事件与业务数据同事务落库，调度器随后投递（至少一次语义）：

```csharp
public class OrderDbContext : OutboxDbContext;   // 继承即获得 OutboxMessages

// 业务事务：事件与数据同一次 SaveChanges
outbox.Add(new OrderPlaced(orderId));
await db.SaveChangesAsync();
```

投递语义、重试与搁置策略详见[后台队列与发件箱](~/docs/queues.md)。

## 相关主题

- [缓存](~/docs/caching.md)——读多写少场景的 single-flight 回源
- [后台队列与发件箱](~/docs/queues.md)——进程内队列与可靠投递
- [定时任务](~/docs/scheduling.md)——cron 驱动的周期任务
