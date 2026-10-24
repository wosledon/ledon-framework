# Ledon.Queues

Ledon Framework 的后台队列与发件箱（Outbox）：进程内异步执行 + 跨进程可靠投递。

```csharp
builder.Services.AddLedonQueues(o => o.WorkerCount = 4);

await queue.EnqueueAsync(async (scope, token) =>
{
    var sender = scope.GetRequiredService<IEmailSender>();   // 独立 DI 作用域
    await sender.SendAsync(token);
});
```

有界队列（默认容量 1000，天然背压）、多 worker、单条失败不拖垮 worker。**进程重启丢失未执行任务**——对丢任务零容忍的场景用发件箱：

```csharp
public class OrderDbContext : OutboxDbContext;   // 继承即获得 OutboxMessages

// 业务事务：事件与数据同一次 SaveChanges
outbox.Add(new OrderPlaced(orderId));
await db.SaveChangesAsync();   // 调度器随后投递并标记
```

发件箱至少一次语义：单条失败记重试不阻塞后续，超限搁置留待人工处理。

完整文档：<https://github.com/wosledon/ledon-framework>
