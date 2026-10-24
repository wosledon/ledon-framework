using System.Text;
using System.Text.Json;
using global::RabbitMQ.Client;

namespace Ledon.Queues.RabbitMQ;

/// <summary>
/// RabbitMQ 传输：把集成事件发布到 Topic 交换机（路由键 = 事件类型全名）。
/// 与 <see cref="IIntegrationEventTransport"/> 契约配合：发件箱调度器投递时调用。
/// </summary>
internal sealed class RabbitMqIntegrationEventTransport : IIntegrationEventTransport
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IConnection _connection;
    private readonly RabbitMqOptions _options;

    public RabbitMqIntegrationEventTransport(IConnection connection, RabbitMqOptions options)
    {
        _connection = connection;
        _options = options;
    }

    public async Task PublishAsync(IIntegrationEvent @event, string? messageId, CancellationToken cancellationToken = default)
    {
        var eventType = @event.GetType();

        await using var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Topic,
            durable: _options.Durable,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var properties = new BasicProperties
        {
            // 程序集限定名：与 OutboxMessage.Type 约定一致（跨程序集反序列化）
            Type = eventType.AssemblyQualifiedName ?? eventType.FullName!,
            ContentType = "application/json",
            DeliveryMode = (DeliveryModes)(_options.PersistentMessages ? 2 : 1),
            // 发件箱消息 Id：消费侧据此做收件箱去重（至少一次投递 + 恰好一次处理）
            MessageId = messageId ?? Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        var body = new ReadOnlyMemory<byte>(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(@event, eventType, SerializerOptions)));

        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: eventType.FullName!,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
