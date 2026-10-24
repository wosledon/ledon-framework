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
