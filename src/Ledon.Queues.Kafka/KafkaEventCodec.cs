using System.Text.Json;
using Ledon.Queues;

namespace Ledon.Queues.Kafka;

/// <summary>主题名与消息载荷的编解码约定（发布侧/消费侧共用，单测可直接覆盖）。</summary>
public static class KafkaEventCodec
{
    /// <summary>消息头：事件类型全名（程序集限定名，与 OutboxMessage.Type 一致）。</summary>
    public const string EventTypeHeader = "ledon-event-type";

    /// <summary>消息头：发件箱消息 Id（收件箱去重凭据）。</summary>
    public const string MessageIdHeader = "ledon-message-id";

    private static readonly JsonSerializerOptions SerializerOptions = new();

    /// <summary>事件类型 → 主题名（前缀 + 类型全名）。</summary>
    public static string TopicFor(Type eventType, string topicPrefix) =>
        $"{topicPrefix}{eventType.FullName}";

    /// <summary>事件实例 → 主题名。</summary>
    public static string TopicFor(IIntegrationEvent @event, string topicPrefix) =>
        TopicFor(@event.GetType(), topicPrefix);

    /// <summary>事件实例 → JSON 载荷（含运行时类型，跨程序集反序列化）。</summary>
    public static string Serialize(IIntegrationEvent @event) =>
        JsonSerializer.Serialize(@event, @event.GetType(), SerializerOptions);

    /// <summary>
    /// 反序列化。优先用 <paramref name="eventType"/>（消息头里的类型全名）；
    /// 类型加载失败时抛 <see cref="InvalidOperationException"/>（消费侧记录并重投）。
    /// </summary>
    public static IIntegrationEvent Deserialize(string eventType, string payload)
    {
        var type = Type.GetType(eventType)
            ?? throw new InvalidOperationException($"无法加载事件类型 {eventType}（确认消费方程序集引用了事件契约）");

        var boxed = JsonSerializer.Deserialize(payload, type, SerializerOptions)
            ?? throw new InvalidOperationException($"事件 {eventType} 载荷反序列化为 null");

        return (IIntegrationEvent)boxed;
    }
}
