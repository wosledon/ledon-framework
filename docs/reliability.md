# 可靠投递：发件箱维护与收件箱去重

消息中间件是**至少一次**投递。Ledon 用两个互补机制把"至少一次"收敛为可运营的可靠性：

- **发件箱（Outbox）**：写侧——事件与业务数据同事务落库，保证不丢；
- **收件箱（Inbox）**：读侧——按消息标识去重，保证重复投递被跳过。

## 发件箱维护：清理与死信

已处理消息只标记不删除会无限增长；超过重试上限的消息会被搁置（死信），不会自愈。调度器按 `CleanupInterval`（默认 1 小时）自动做两件事：

```csharp
builder.Services.AddLedonOutbox(o =>
{
    o.Retention = TimeSpan.FromDays(7);    // 已处理消息保留时长（TimeSpan.Zero = 不清理）
    o.CleanupInterval = TimeSpan.FromHours(1);
});
```

- 清理：删除 `Retention` 之前的已处理消息（Information 日志 + `ledon.outbox.cleaned` 指标）；
- 死信告警：每次维护把搁置消息数打到 Warning 日志（含消息 Id 示例），并累计 `ledon.outbox.dead_letters` 指标。

### 死信查询与重放

```csharp
public interface IOutboxStore
{
    Task<int> DeleteProcessedAsync(TimeSpan retention, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int batchSize, CancellationToken ct = default);
    Task<bool> ReviveAsync(Guid id, CancellationToken ct = default);   // 清零重试计数，重新投递
}
```

典型运维动作：从死信清单定位失败原因（`OutboxMessage.Error` 保留了现场）→ 修复依赖 → `ReviveAsync(id)` 重放。多实例部署可把查询/重放暴露成受权限保护的管理端点。

## 收件箱（Inbox）

```csharp
builder.Services.AddLedonInbox();   // 存储复用 OutboxDbContext（同库一张 InboxMessages 表）
```

注册后，集成事件分发按**消息标识**认领：

```text
TryClaim(messageId) → 分发 → MarkCompleted(messageId)
```

- 已处理完成的重复投递：直接跳过（`ledon.inbox.skipped` 指标）；
- 认领未超时（默认 5 分钟）的在途消息：跳过——不会并发处理同一条；
- 上次处理进程崩溃留下的**陈旧认领**：超时后允许重新认领重试——至少一次语义不被破坏。

消息标识的来源：发件箱投递时用 `OutboxMessage.Id`；broker 投递时用传输写进消息属性的 Id（RabbitMQ `MessageId` / Kafka `ledon-message-id` 头）。**不注册 `IInboxStore` 时行为与之前完全一致**（不去重），可灰度启用。

## 指标

`Ledon.Queues` Meter（`AddLedonObservability()` 自动收集，也可用 MeterListener 自取）：

| 指标                            | 含义                       |
| ------------------------------- | -------------------------- |
| `ledon.outbox.dispatched`       | 投递成功的消息数           |
| `ledon.outbox.retried`          | 投递失败进入重试的次数     |
| `ledon.outbox.dead_letters`     | 搁置（重试超上限）的消息数 |
| `ledon.outbox.cleaned`          | 清理的超保留期消息数       |
| `ledon.inbox.skipped`           | 收件箱去重跳过的重复投递   |
| `ledon.queue.processed/failed`  | 后台队列任务成败           |

## 相关主题

- [后台队列与发件箱](~/docs/queues.md)——投递语义与传输抽象
- [Kafka 传输](~/docs/kafka.md)——broker 投递侧的收件箱配合
