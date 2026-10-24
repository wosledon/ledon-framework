# 后台队列与发件箱

两个层次的问题，两个包内能力：

- **后台队列**：进程内异步执行（ Channel + 多 worker ），fire-and-forget，进程重启即丢失；
- **发件箱（Outbox）**：跨进程可靠投递（至少一次），事件与业务数据同事务落库。

## 后台队列

```csharp
builder.Services.AddLedonQueues(o => o.WorkerCount = 4);   // 4 个并发 worker

await queue.EnqueueAsync(async (scope, token) =>
{
    var sender = scope.GetRequiredService<IEmailSender>();   // 独立 DI 作用域
    await sender.SendAsync(token);
});
```

- 每个任务在**独立 DI 作用域**内执行（Scoped 服务安全解析，不会串味）；
- 有界队列（默认容量 1000，满时写入方等待）——天然背压，不会 OOM；
- 单条任务抛异常只记日志，不拖垮 worker，后续任务照常执行。

**进程重启会丢失未执行任务**——对丢任务零容忍的场景用发件箱。

## 发件箱（Outbox）

```csharp
public class OrderDbContext : OutboxDbContext;   // 继承即获得 OutboxMessages 表

// 业务事务：事件与数据同一次 SaveChanges（原子性）
outbox.Add(new OrderPlaced(orderId));
await db.SaveChangesAsync();   // 调度器随后投递并标记
```

```csharp
public record OrderPlaced(string OrderId) : IIntegrationEvent;

public class OrderPlacedHandler : IIntegrationEventHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced e, CancellationToken ct = default) { /* 发邮件/通知 */ }
}
```

- **至少一次语义**：投递成功才标记；失败记重试次数，下个轮询周期重试；
- 单条失败不阻塞后续消息；超过重试上限**搁置**，留待人工处理（监控搁置表即可发现毒消息）；
- 处理器自动发现、自动注入，与进程内事件（`IEventHandler<T>`）写法一致。

## 消息中间件传输

注册 `IIntegrationEventTransport` 后，发件箱把消息投给传输（broker）而非进程内 handler——消费方（另一个服务/进程）订阅 broker 后在自己的容器里派发给本地 handler：

```csharp
// 发布方：注册传输即可，OutboxDispatcher 自动改走 broker
builder.Services.AddSingleton<IIntegrationEventTransport>(new MyRabbitMqTransport(...));
```

现成实现：`Ledon.Queues.RabbitMQ` 包（`AddLedonRabbitMqTransport()`，含发布与订阅消费两侧）。

```csharp
builder.Services.AddLedonRabbitMqTransport(o =>
    o.ConnectionString = "amqp://user:pass@localhost:5672");
```

发布方与消费方装同一个包：发布侧由发件箱自动投递；消费侧自动订阅本服务队列（默认 `{交换机}.{服务名}`，多实例竞争消费）并派发到进程内 handler——消费方代码零改动。单条处理失败不确认，broker 重新投递（至少一次）。

传输失败由发件箱记录重试（至少一次语义不变）；投递成功才标记已处理。

## 怎么选

| 需求                       | 选择         |
| -------------------------- | ------------ |
| 请求内触发的异步副作用     | 后台队列     |
| 跨服务/跨进程通知          | 发件箱       |
| 事务提交后必须发生的动作   | 发件箱       |
| 允许丢失的临时计算         | 后台队列     |
