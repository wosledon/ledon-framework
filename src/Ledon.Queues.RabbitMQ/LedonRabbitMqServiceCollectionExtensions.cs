using System.Diagnostics.CodeAnalysis;
using Ledon.Queues;
using Ledon.Queues.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using global::RabbitMQ.Client;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// RabbitMQ 传输入口。发布方与消费方都装这个包：
/// <code>
/// builder.Services.AddLedonRabbitMqTransport(o => o.ConnectionString = "amqp://user:pass@host:5672");
/// </code>
/// 之后：发件箱调度器把集成事件投给 RabbitMQ（发布侧）；
/// 同时启动消费侧——订阅本服务队列并派发给进程内 IIntegrationEventHandler。
/// </summary>
public static class LedonRabbitMqServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的集成事件反序列化基于反射（Type.GetType）。" +
        "NativeAOT 或裁剪发布时，事件类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonRabbitMqTransport(
        this IServiceCollection services,
        Action<RabbitMqOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RabbitMqOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);

        // 共享一条连接（线程安全，发布与消费共用；关闭时随容器释放）
        services.AddSingleton<IConnection>(_ =>
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.ConnectionString),
            };
            return factory.CreateConnectionAsync().GetAwaiter().GetResult();
        });

        services.AddSingleton<IIntegrationEventTransport, RabbitMqIntegrationEventTransport>();
        services.AddHostedService<RabbitMqConsumerService>();

        return services;
    }
}
