using System.Text;
using Confluent.Kafka;

namespace Ledon.Queues.Kafka;

/// <summary>
/// 死信路由：处理失败超过 <see cref="KafkaOptions.MaxDeliveryAttempts"/> 的消息转入
/// <c>{主题}{后缀}</c> 死信主题（保留原始头 + 附加失败原因），不再无限重投阻塞分区。
/// 纯逻辑、无 broker 依赖，单测直接覆盖。
/// </summary>
public static class KafkaDeadLetterRouter
{
    /// <summary>记录投递尝试次数的消息头。</summary>
    public const string DeliveryAttemptsHeader = "ledon-delivery-attempts";

    /// <summary>死信原因消息头。</summary>
    public const string DeadLetterReasonHeader = "ledon-dlq-reason";

    /// <summary>进入死信的时间消息头（UTC ISO-8601）。</summary>
    public const string DeadLetterAtHeader = "ledon-dlq-at";

    /// <summary>死信主题名：<c>{topic}{suffix}</c>。</summary>
    public static string DeadLetterTopicFor(string topic, string suffix) => $"{topic}{suffix}";

    /// <summary>是否应转入死信（attempts 已达上限）。</summary>
    public static bool ShouldDeadLetter(int attempts, int maxAttempts) =>
        maxAttempts > 0 && attempts >= maxAttempts;

    /// <summary>
    /// 构造死信消息：保留原始 key/值与头，附加尝试次数、失败原因、时间。
    /// </summary>
    public static Message<string, string> BuildDeadLetterMessage(
        Message<string, string> original,
        int attempts,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(original);

        var headers = new Headers();
        if (original.Headers is not null)
        {
            foreach (var header in original.Headers)
                headers.Add(header.Key, header.GetValueBytes());
        }

        headers.Add(DeliveryAttemptsHeader, Encoding.UTF8.GetBytes(attempts.ToString()));
        headers.Add(DeadLetterReasonHeader, Encoding.UTF8.GetBytes(reason));
        headers.Add(DeadLetterAtHeader, Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));

        return new Message<string, string>
        {
            Key = original.Key,
            Value = original.Value,
            Headers = headers,
        };
    }
}
