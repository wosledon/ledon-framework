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
- 单条任务抛异常只记日志，不拖垮 worker，后续任务照常执行；
- **停机 drain（默认 5s）**：宿主停止时把队列里已入队未处理的任务继续处理完（有界），
  优雅退出尽量不丢任务；`DrainTimeout = TimeSpan.Zero` 则不 drain（丢弃数量记 Warning）；
  正在执行中的任务仍由停机令牌取消，语义不变。

**进程重启会丢失未执行任务**——对丢任务零容忍的场景用发件箱。

### 什么时候必须用发件箱而不是后台队列

后台队列的存储是进程内 Channel——以下任一条件命中就别用它：

- 进程可能被 kill/崩溃/OOM（队列里的任务随进程消失）；
- 多实例部署且任务必须被处理一次（Channel 不跨实例，且重启后不恢复）；
- 任务与业务数据必须原子（"数据库提交了但邮件没发"无法接受）。

发件箱把任务/事件写进业务数据库（同事务），由调度器投递——进程重启后待投递消息仍在。
需要跨服务投递时再加传输包（RabbitMQ/Kafka）。

### 持久化后台队列（重启不丢任务）

既想要"fire-and-forget 的简单 API"，又要求任务不随进程消失——用持久化队列：

```csharp
builder.Services.AddLedonDurableQueue();   // DbContext 继承 OutboxDbContext 即获得队列表

// 业务代码：与业务数据同事务入队（调用方负责 SaveChanges）
await durableQueue.EnqueueAsync(new DurableJob("send-email", "{\"to\":\"a@b.c\"}"));
await durableQueue.EnqueueAsync(new DurableJob("nightly-report", "{}", Delay: TimeSpan.FromHours(6)));

// 处理器（实现类自动发现，按 Name 匹配）
public class SendEmailHandler : IDurableJobHandler
{
    public string Name => "send-email";
    public Task ExecuteAsync(string payload, CancellationToken ct = default) => /* ... */;
}
```

- 任务落库（与发件箱同库的 `QueuedJobs` 表），进程重启后由调度器继续投递；
- 失败重试至 `MaxRetries`（默认 5），超限搁置（记 Warning，不阻塞后续）；
- 支持延迟任务（`Delay`）、保留期清理（`Retention`，默认 1 天）；
- 与发件箱的分工：**发件箱**管"跨服务领域事件"，**持久化队列**管"本服务后台作业"。

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
- 处理器自动发现、自动注入，与进程内事件（`IEventHandler<T>`）写法一致；
- **认领制投递**：多实例部署时同一条消息只有一个实例会投。每条消息带
  `LeasedBy`/`LeasedUntil`，调度器先原子认领再投递；租约到期未处理完则由其它实例接手
  （节点崩溃兜底）。没有这一步，N 个实例会把同一条消息各投一遍（N 倍重复）；
- 运维快照的"待投递数"走 `CountPendingAsync`（只数不认领），不会把别人正在投的消息抢过来。

### 退避与并发参数

```csharp
builder.Services.AddLedonOutbox(o =>
{
    o.Lease = TimeSpan.FromMinutes(2);   // 认领租约：崩溃后多久有别的实例接手
});
```

持久化队列同样支持（`LedonDurableQueueOptions.Lease`）。

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
