using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Ledon.Queues;
using Ledon.Queues.Kafka;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Kafka 传输入口（发件箱 ↔ Kafka，跨服务投递）：
/// <code>
/// builder.Services.AddLedonOutbox();
/// builder.Services.AddLedonKafkaTransport(o =&gt; o.BootstrapServers = "localhost:9092");
/// // 之后发件箱消息自动投到 Kafka；本服务也自动订阅已发现的集成事件主题并派发进程内 handler
/// </code>
/// 至少一次语义：发布失败由发件箱重试，消费失败不提交偏移量由 broker 重投；
/// 配合 <c>AddLedonInbox()</c> 获得"恰好一次处理"。
/// </summary>
public static class LedonKafkaServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的集成事件发现与反序列化基于反射（Assembly.GetTypes / Type.GetType）。" +
        "NativeAOT 或裁剪发布时，事件类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonKafkaTransport(
        this IServiceCollection services,
        Action<KafkaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new KafkaOptions();
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
            throw new InvalidOperationException("AddLedonKafkaTransport: 必须配置 BootstrapServers");

        services.AddSingleton(options);

        // 生产者（进程内共享）：按 key 保序，失败由发件箱重试兜底
        services.AddSingleton<IProducer<string, string>>(_ => new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = options.BootstrapServers }).Build());

        services.AddSingleton<IIntegrationEventTransport, KafkaIntegrationEventTransport>();
        services.AddHostedService<KafkaConsumerService>();

        return services;
    }
}
