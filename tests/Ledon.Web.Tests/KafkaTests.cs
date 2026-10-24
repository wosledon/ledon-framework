using System.Text;
using Confluent.Kafka;
using Ledon.Queues;
using Ledon.Queues.Kafka;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// Kafka 传输测试：编解码约定（主题名/序列化）与注册行为；
/// 完整链路（Outbox → Kafka → 消费侧 → handler）需真实 broker，
/// 设置 LEDON_TEST_KAFKA_BOOTSTRAP 环境变量后启用（CI/本地默认跳过）。
/// </summary>
public class KafkaTests : IDisposable
{
    private WebApplication? _app;

    [Fact]
    public void Topic_name_is_prefix_plus_event_type_full_name()
    {
        Assert.Equal(
            "ledon.Ledon.Web.Tests.Fixtures.OrderPlaced",
            KafkaEventCodec.TopicFor(typeof(OrderPlaced), "ledon."));
    }

    [Fact]
    public void Event_payload_roundtrips_through_json()
    {
        var payload = KafkaEventCodec.Serialize(new OrderPlaced("order-1"));

        var @event = KafkaEventCodec.Deserialize(typeof(OrderPlaced).AssemblyQualifiedName!, payload);

        var orderPlaced = Assert.IsType<OrderPlaced>(@event);
        Assert.Equal("order-1", orderPlaced.OrderId);
    }

    [Fact]
    public void Deserialize_unknown_event_type_fails_with_guidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => KafkaEventCodec.Deserialize("No.Such.Event", "{}"));

        Assert.Contains("No.Such.Event", ex.Message);
    }

    [Fact]
    public void AddLedonKafkaTransport_registers_transport_and_consumer()
    {
        var services = new ServiceCollection();
        services.AddLedonKafkaTransport(o => o.BootstrapServers = "localhost:9092");

        // 传输（发件箱 → Kafka）
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IIntegrationEventTransport)
            && descriptor.ImplementationType?.Name.Contains("Kafka", StringComparison.Ordinal) == true);

        // 消费侧托管服务
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && descriptor.ImplementationType?.Name.Contains("Kafka", StringComparison.Ordinal) == true);

        // 生产者
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(Confluent.Kafka.IProducer<string, string>));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IIntegrationEventTransport>());
    }

    [Fact]
    public void AddLedonKafkaTransport_requires_bootstrap_servers()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(
            () => services.AddLedonKafkaTransport(o => o.BootstrapServers = ""));
    }

    // ---------- 死信路由 ----------

    [Fact]
    public void Dead_letter_topic_is_original_topic_plus_suffix()
    {
        Assert.Equal(
            "ledon.MyApp.OrderPlaced.dlq",
            KafkaDeadLetterRouter.DeadLetterTopicFor("ledon.MyApp.OrderPlaced", ".dlq"));
    }

    [Fact]
    public void Dead_letter_triggers_only_after_max_attempts()
    {
        Assert.False(KafkaDeadLetterRouter.ShouldDeadLetter(3, 5));
        Assert.False(KafkaDeadLetterRouter.ShouldDeadLetter(4, 5));
        Assert.True(KafkaDeadLetterRouter.ShouldDeadLetter(5, 5));
        Assert.True(KafkaDeadLetterRouter.ShouldDeadLetter(9, 5));

        // 上限设为 0/负值 = 不启用死信（无限重投）
        Assert.False(KafkaDeadLetterRouter.ShouldDeadLetter(100, 0));
        Assert.False(KafkaDeadLetterRouter.ShouldDeadLetter(100, -1));
    }

    [Fact]
    public void Dead_letter_message_preserves_payload_and_headers_and_adds_diagnostics()
    {
        var original = new Message<string, string>
        {
            Key = "order-1",
            Value = "{\"orderId\":\"order-1\"}",
            Headers = new Headers
            {
                { KafkaEventCodec.EventTypeHeader, Encoding.UTF8.GetBytes("MyApp.OrderPlaced") },
            },
        };

        var dead = KafkaDeadLetterRouter.BuildDeadLetterMessage(original, attempts: 5, reason: "handler boom");

        Assert.Equal("order-1", dead.Key);
        Assert.Equal("{\"orderId\":\"order-1\"}", dead.Value);

        string? Header(string key) => dead.Headers
            .FirstOrDefault(header => header.Key == key) is { } found
                ? Encoding.UTF8.GetString(found.GetValueBytes())
                : null;

        Assert.Equal("MyApp.OrderPlaced", Header(KafkaEventCodec.EventTypeHeader));   // 原始头保留
        Assert.Equal("5", Header(KafkaDeadLetterRouter.DeliveryAttemptsHeader));
        Assert.Equal("handler boom", Header(KafkaDeadLetterRouter.DeadLetterReasonHeader));
        Assert.NotNull(Header(KafkaDeadLetterRouter.DeadLetterAtHeader));
    }

    [Fact]
    public void Kafka_transport_registers_broker_health_check()
    {
        var services = new ServiceCollection();
        services.AddLedonKafkaTransport(o => o.BootstrapServers = "localhost:9092");

        using var provider = services.BuildServiceProvider();

        // 健康检查登记在 HealthCheckServiceOptions（就绪探针聚合它）
        var registrations = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations;

        Assert.Contains(registrations, registration =>
            registration.Name == "kafka"
            && registration.Factory is not null);
    }

    // ---------- broker 端到端（需 Kafka；设置 LEDON_TEST_KAFKA_BOOTSTRAP 后启用） ----------

    [Fact]
    public async Task Outbox_publishes_via_kafka_to_local_handler()
    {
        var bootstrap = Environment.GetEnvironmentVariable("LEDON_TEST_KAFKA_BOOTSTRAP");
        if (bootstrap is null)
            return;   // 无 broker 环境：跳过（CI/本地默认不含 Kafka）

        OutboxSignal.Delivered = false;

        var dbPath = Path.Combine(AppContext.BaseDirectory, $"outbox-kafka-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(100));
        builder.Services.AddLedonKafkaTransport(o => o.BootstrapServers = bootstrap);
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();

            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            store.Add(new OrderPlaced("order-kafka"));
            await db.SaveChangesAsync();
        }

        // 全链路：Outbox → Kafka → 消费侧 → 进程内 handler
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!OutboxSignal.Delivered && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Assert.True(OutboxSignal.Delivered, "事件未经 Kafka 送达进程内 handler（检查 LEDON_TEST_KAFKA_BOOTSTRAP）");
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}
