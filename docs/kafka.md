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
