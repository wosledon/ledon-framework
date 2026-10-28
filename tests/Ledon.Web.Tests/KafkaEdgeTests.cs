using System.Text;
using Confluent.Kafka;
using Ledon.Queues;
using Ledon.Queues.Kafka;
using Ledon.Web.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ledon.Web.Tests;

/// <summary>Kafka 边界：死信路由、消息头、主题前缀、配置校验。</summary>
public class KafkaEdgeTests
{
    [Fact]
    public void Dead_letter_router_honors_attempt_boundaries()
    {
        Assert.False(KafkaDeadLetterRouter.ShouldDeadLetter(4, 5));
        Assert.True(KafkaDeadLetterRouter.ShouldDeadLetter(5, 5));
        Assert.False(KafkaDeadLetterRouter.ShouldDeadLetter(5, 0));   // 0 = 不启用死信
        Assert.Equal("orders.dlq", KafkaDeadLetterRouter.DeadLetterTopicFor("orders", ".dlq"));
    }

    [Fact]
    public void Dead_letter_message_preserves_payload_and_adds_diagnostics()
    {
        var original = new Message<string, string>
        {
            Key = "order-1",
            Value = "{}",
            Headers = new Headers { { "trace", Encoding.UTF8.GetBytes("abc") } },
        };

        var dead = KafkaDeadLetterRouter.BuildDeadLetterMessage(original, 7, "handler failed");

        Assert.Equal("order-1", dead.Key);
        Assert.Equal("{}", dead.Value);
        Assert.Equal("abc", Encoding.UTF8.GetString(dead.Headers.GetLastBytes("trace")));
        Assert.Equal("7", Encoding.UTF8.GetString(dead.Headers.GetLastBytes(KafkaDeadLetterRouter.DeliveryAttemptsHeader)));
        Assert.Equal("handler failed", Encoding.UTF8.GetString(dead.Headers.GetLastBytes(KafkaDeadLetterRouter.DeadLetterReasonHeader)));
        Assert.True(dead.Headers.TryGetLastBytes(KafkaDeadLetterRouter.DeadLetterAtHeader, out _));
    }

    [Fact]
    public async Task Transport_publishes_to_prefixed_topic_with_diagnostic_headers()
    {
        var producer = new Mock<IProducer<string, string>>();
        Message<string, string>? captured = null;
        string? capturedTopic = null;
        producer
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(),
                It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Message<string, string>, CancellationToken>((topic, message, _) =>
            {
                capturedTopic = topic;
                captured = message;
            })
            .ReturnsAsync(new DeliveryResult<string, string>());

        var transport = new KafkaIntegrationEventTransport(
            producer.Object,
            new KafkaOptions { TopicPrefix = "test." });

        await transport.PublishAsync(new OrderPlaced("order-1"), "message-1");

        Assert.Equal("test.Ledon.Web.Tests.Fixtures.OrderPlaced", capturedTopic);
        Assert.NotNull(captured);
        Assert.Equal(typeof(OrderPlaced).FullName, captured!.Key);
        Assert.Equal("message-1", Encoding.UTF8.GetString(captured.Headers.GetLastBytes(KafkaEventCodec.MessageIdHeader)));
        Assert.NotNull(captured.Headers.GetLastBytes(KafkaEventCodec.EventTypeHeader));
    }

    [Fact]
    public void Registration_rejects_blank_bootstrap_servers()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddLedonKafkaTransport(options => options.BootstrapServers = "   "));

        Assert.Contains("BootstrapServers", exception.Message, StringComparison.Ordinal);
    }
}
