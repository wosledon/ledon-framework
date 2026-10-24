# Kafka 传输

发件箱 ↔ Kafka 的跨服务投递。与 RabbitMQ 传输同一套抽象（`IIntegrationEventTransport`），二选一。

## 接入

```csharp
builder.Services.AddLedonOutbox();
builder.Services.AddLedonKafkaTransport(o =>
{
    o.BootstrapServers = "localhost:9092";
    // o.TopicPrefix = "ledon.";      // 主题 = 前缀 + 事件类型全名
    // o.GroupId = "orders-service";  // 默认取服务名（LEDON_SERVICE_NAME / 入口程序集名）
});
```

装包即两端就绪：

- **发布侧**：发件箱调度器把消息投到 `{TopicPrefix}{事件类型全名}` 主题（发送确认失败由发件箱重试）；
- **消费侧**：自动订阅"已发现的集成事件类型"对应的主题（`SubscribeDiscoveredEventTypes`，可关），反序列化后派发给进程内 `IIntegrationEventHandler<T>`；同消费组多实例 = 竞争消费。

## 投递语义

- 至少一次：发布失败由发件箱重试；消费失败不提交偏移量，broker 重投；
- 恰好一次处理：配 `AddLedonInbox()`，broker 重投的消息按 `ledon-message-id` 头去重（见[可靠投递](~/docs/reliability.md)）；
- 事件类型经消息头 `ledon-event-type` 传递（程序集限定名，与 `OutboxMessage.Type` 一致）——消费方程序集须引用事件契约。

## 死信主题（DLQ）

处理失败超过 `MaxDeliveryAttempts`（默认 5 次）的消息转入 `{主题}{后缀}`（默认 `.dlq`），不再无限重投阻塞分区：

```csharp
builder.Services.AddLedonKafkaTransport(o =>
{
    o.BootstrapServers = "localhost:9092";
    o.MaxDeliveryAttempts = 5;      // 0 或负值 = 不启用死信（无限重投）
    o.DeadLetterTopicSuffix = ".dlq";
});
```

- 死信消息保留原始 key/值与头，并附加 `ledon-delivery-attempts`（尝试次数）、`ledon-dlq-reason`（失败原因）、`ledon-dlq-at`（时间）；
- 进入死信后提交偏移量——毒消息不再阻塞分区；重置消费组偏移量可重放；
- 尝试计数是进程内的（键 = 主题/分区/偏移量），重启后重置——死信是"尽力而为"的护栏；
- 路由逻辑在 `KafkaDeadLetterRouter`（纯静态、无 broker 依赖），单测直接覆盖。

## broker 健康检查

配了传输入口即自动登记连通性健康检查（就绪探针用，未接健康检查端点时是惰性登记）：

```csharp
builder.Services.AddLedonKafkaTransport(o => o.BootstrapServers = "localhost:9092");
builder.Services.AddLedonHealthChecks();
app.MapLedonReadiness();   // Kafka 挂了 → 503 摘流；/health/live 仍 200 不重启
```

RabbitMQ 传输（`AddLedonRabbitMqTransport`）同样自动登记 `rabbitmq` 检查。

## 编解码约定

编解码逻辑是独立可测的静态类（`KafkaEventCodec`）：主题名、序列化、类型解析，单测直接覆盖，不需要 broker。

## 端到端测试

完整链路需要真实 broker。设置环境变量后测试自动启用（CI/本地默认跳过）：

```powershell
$env:LEDON_TEST_KAFKA_BOOTSTRAP = "localhost:9092"
dotnet test
```

## 相关主题

- [后台队列与发件箱](~/docs/queues.md)——传输抽象与进程内投递
- [可靠投递](~/docs/reliability.md)——收件箱去重与死信管理
