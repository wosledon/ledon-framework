using System.Text;
using global::RabbitMQ.Client;
using Ledon.Queues;
using Ledon.Queues.RabbitMQ;
using Ledon.Web.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

namespace Ledon.Web.Tests;

/// <summary>RabbitMQ 边界：交换机声明/发布头、健康检查、注册。</summary>
public class RabbitMqEdgeTests
{
    [Fact]
    public void Options_have_safe_defaults()
    {
        var options = new RabbitMqOptions();

        Assert.Equal("ledon.integration-events", options.ExchangeName);
        Assert.Equal("#", options.BindingPattern);
        Assert.Equal(20, options.PrefetchCount);
        Assert.True(options.Durable);
        Assert.True(options.PersistentMessages);
    }

    [Fact]
    public async Task Transport_declares_exchange_and_publishes_with_headers()
    {
        var channel = new Mock<IChannel>();
        string? publishedExchange = null;
        string? publishedKey = null;
        BasicProperties? publishedProperties = null;
        ReadOnlyMemory<byte> publishedBody = default;

        channel
            .Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (exchange, routingKey, _, properties, body, _) =>
                {
                    publishedExchange = exchange;
                    publishedKey = routingKey;
                    publishedProperties = properties;
                    publishedBody = body;
                })
            .Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var connection = new Mock<IConnection>();
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);

        var transport = new RabbitMqIntegrationEventTransport(
            connection.Object,
            new RabbitMqOptions { ExchangeName = "events", PersistentMessages = false });

        await transport.PublishAsync(new OrderPlaced("order-1"), "message-1");

        Assert.Equal("events", publishedExchange);
        Assert.Equal(typeof(OrderPlaced).FullName, publishedKey);
        Assert.NotNull(publishedProperties);
        Assert.Equal("message-1", publishedProperties!.MessageId);
        Assert.Equal("application/json", publishedProperties.ContentType);
        Assert.Equal(typeof(OrderPlaced).AssemblyQualifiedName, publishedProperties.Type);
        Assert.Contains("order-1", Encoding.UTF8.GetString(publishedBody.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_check_reports_unhealthy_when_connection_is_closed()
    {
        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(false);

        var result = await new RabbitMqHealthCheck(connection.Object)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Health_check_reports_healthy_when_channel_can_be_opened()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.Dispose()).Verifiable();

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);

        var result = await new RabbitMqHealthCheck(connection.Object)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        channel.Verify();
    }
}
