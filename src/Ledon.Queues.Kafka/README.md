# Ledon.Queues.Kafka

发件箱 ↔ Kafka 的跨服务投递，与 RabbitMQ 传输共用 `IIntegrationEventTransport` 抽象，二选一。

```csharp
builder.Services.AddLedonOutbox();
builder.Services.AddLedonKafkaTransport(o =>
{
    o.BootstrapServers = "localhost:9092";
    // o.TopicPrefix = "ledon.";      // 主题 = 前缀 + 事件类型全名
    // o.GroupId = "orders-service";  // 默认取服务名
});
```

- 发布侧：发件箱调度器把消息投到 `{TopicPrefix}{事件类型全名}`，发送失败由发件箱重试；
- 消费侧：自动订阅已发现的集成事件类型，反序列化后派发给进程内 `IIntegrationEventHandler<T>`；
  同消费组多实例 = 竞争消费；
- 至少一次投递：消费失败不提交偏移量，由 broker 重投；配合 `AddLedonInbox()` 获得恰好一次处理；
- 死信主题：单条消息超过 `MaxDeliveryAttempts`（默认 5）后转入 `{主题}{DeadLetterTopicSuffix}`
  （默认 `.dlq`），保留原始 key/值与头，并附加尝试次数、原因和时间；
- 配好传输入口即自动登记 broker 连通性健康检查（就绪探针用）；
- 未配置 broker 时消费循环不启动，应用仍可正常构建/启动。

完整文档：<https://github.com/wosledon/ledon-framework>
