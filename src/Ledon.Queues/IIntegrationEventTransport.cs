namespace Ledon.Queues;

/// <summary>
/// 集成事件传输（消息中间件出口）。注册后，发件箱调度器把消息投给传输（RabbitMQ/Kafka/Azure Service Bus……）
/// 而非进程内 <see cref="IIntegrationEventHandler{TEvent}"/>——消费方（另一个服务/进程）从 broker 订阅后
/// 在自己的容器里派发给本地 handler。不注册时保持进程内投递（默认）。
/// </summary>
public interface IIntegrationEventTransport
{
    /// <summary>把集成事件发布到传输通道。失败时由发件箱记录重试（至少一次语义）。</summary>
    Task PublishAsync(IIntegrationEvent @event, CancellationToken cancellationToken = default);
}
