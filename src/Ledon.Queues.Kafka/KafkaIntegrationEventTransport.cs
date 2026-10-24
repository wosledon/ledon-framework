using System.Text;
using Confluent.Kafka;
using Ledon.Queues;

namespace Ledon.Queues.Kafka;

/// <summary>
/// Kafka 传输：把集成事件发布到「前缀 + 事件类型全名」主题。
/// 与 <see cref="IIntegrationEventTransport"/> 契约配合：发件箱调度器投递时调用。
/// 发布失败由发件箱记录重试（至少一次语义）。
/// </summary>
internal sealed class KafkaIntegrationEventTransport : IIntegrationEventTransport
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _options;

    public KafkaIntegrationEventTransport(IProducer<string, string> producer, KafkaOptions options)
    {
        _producer = producer;
        _options = options;
    }

    public async Task PublishAsync(IIntegrationEvent @event, string? messageId, CancellationToken cancellationToken = default)
    {
        var eventType = @event.GetType();
        var topic = KafkaEventCodec.TopicFor(eventType, _options.TopicPrefix);

        var message = new Message<string, string>
        {
            Key = eventType.FullName!,
            Value = KafkaEventCodec.Serialize(@event),
            Headers = new Headers
            {
                { KafkaEventCodec.EventTypeHeader, Encoding.UTF8.GetBytes(eventType.AssemblyQualifiedName ?? eventType.FullName!) },
                { KafkaEventCodec.MessageIdHeader, Encoding.UTF8.GetBytes(messageId ?? Guid.NewGuid().ToString()) },
            },
        };

        // 发送确认（IsCanceled/错误都由发件箱捕获并重试）
        await _producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);
    }
}
