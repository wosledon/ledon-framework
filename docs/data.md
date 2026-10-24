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

跨进程/要求可靠投递的集成事件：

```csharp
public class OrderDbContext : OutboxDbContext;   // 继承即获得 OutboxMessages

// 业务事务：事件与数据同一次 SaveChanges
outbox.Add(new OrderPlaced(orderId));
await db.SaveChangesAsync();
```

调度器按轮询间隔投递（至少一次语义）：单条失败记重试不阻塞后续，超重试上限搁置留待人工处理。

```csharp
public record OrderPlaced(string OrderId) : IIntegrationEvent;

public class OrderPlacedHandler : IIntegrationEventHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced e, CancellationToken ct = default) { /* ... */ }
}
```

## 缓存

```csharp
builder.Services.AddLedonCaching();                          // 进程内（single-flight）
builder.Services.AddLedonDistributedCaching(o =>             // 多实例共享
    o.RedisConnectionString = "localhost:6379");
```

## 后台队列 / 定时任务

```csharp
builder.Services.AddLedonQueues(o => o.WorkerCount = 4);
await queue.EnqueueAsync(async (scope, token) => { /* 独立作用域 */ });

builder.Services.AddLedonScheduling();
public class CleanupTask : IScheduledTask
{
    public string CronExpression => "0 3 * * *";
    public Task ExecuteAsync(CancellationToken ct = default) { /* ... */ }
}
```
