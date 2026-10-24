namespace Ledon.Queues;

/// <summary>
/// 集成事件传输（消息中间件出口）。注册后，发件箱调度器把消息投给传输（RabbitMQ/Kafka/Azure Service Bus……）
/// 而非进程内 <see cref="IIntegrationEventHandler{TEvent}"/>——消费方（另一个服务/进程）从 broker 订阅后
/// 在自己的容器里派发给本地 handler。不注册时保持进程内投递（默认）。
/// </summary>
public interface IIntegrationEventTransport
{
    /// <summary>
    /// 把集成事件发布到传输通道。失败时由发件箱记录重试（至少一次语义）。
    /// </summary>
    /// <param name="event">集成事件。</param>
    /// <param name="messageId">
    /// 投递标识（发件箱消息 Id）：传输应把它写进 broker 消息属性（如 RabbitMQ MessageId），
    /// 消费侧据此做收件箱去重；无消息源标识时传 null。
    /// </param>
    Task PublishAsync(IIntegrationEvent @event, string? messageId, CancellationToken cancellationToken = default);
}
