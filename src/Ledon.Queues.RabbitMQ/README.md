# Ledon.Queues.RabbitMQ

Ledon Framework 的消息中间件传输：发件箱 ↔ RabbitMQ，集成事件跨服务可靠投递。

```csharp
// 发布方与消费方装同一个包即可，一行接入
builder.Services.AddLedonRabbitMqTransport(o =>
    o.ConnectionString = "amqp://user:pass@localhost:5672");
```

- **发布侧**：`OutboxDispatcher` 自动把集成事件投到 Topic 交换机（路由键 = 事件类型全名），至少一次语义不变；
- **消费侧**：自动启动消费者，订阅本服务队列（默认 `{交换机}.{服务名}`，同服务多实例竞争消费），反序列化后派发给进程内 `IIntegrationEventHandler<T>`——消费方代码零改动；
- **故障语义**：单条处理失败不确认（nack），broker 重新投递；连接断开由 RabbitMQ.Client 自动重连。

```csharp
builder.Services.AddLedonRabbitMqTransport(o =>
{
    o.ExchangeName = "myapp.events";       // 发布/订阅共用
    o.QueueName = "myapp.notifications";   // 默认 {交换机}.{服务名}
    o.BindingPattern = "#";                // 路由键模式（默认收全部事件）
    o.PrefetchCount = 20;                  // 预取上限
});
```

服务名默认取入口程序集名，可用环境变量 `LEDON_SERVICE_NAME` 覆盖。

完整文档：<https://github.com/wosledon/ledon-framework>
