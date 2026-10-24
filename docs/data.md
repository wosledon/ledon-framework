# 数据与发件箱

## EF Core

```csharp
public class TodoDbContext : DbContext, ILedonDbContext { /* ... */ }
```

实现 `ILedonDbContext` 即被自动注册，并在启动时（先于所有模块初始化）自动建库/迁移——有挂起迁移走 `Migrate`，否则 `EnsureCreated`。

```csharp
builder.Services.AddLedonData(o => o.AutoMigrate = false);   // 自己管理 schema 时关闭
```

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
