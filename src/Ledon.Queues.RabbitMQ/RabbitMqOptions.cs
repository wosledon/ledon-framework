namespace Ledon.Queues.RabbitMQ;

/// <summary>RabbitMQ 传输参数。</summary>
public sealed class RabbitMqOptions
{
    /// <summary>连接串，默认 amqp://localhost:5672（可含凭据：amqp://user:pass@host:5672/vhost）。</summary>
    public string ConnectionString { get; set; } = "amqp://localhost:5672";

    /// <summary>Topic 交换机名（发布方与订阅方共用），默认 ledon.integration-events。</summary>
    public string ExchangeName { get; set; } = "ledon.integration-events";

    /// <summary>
    /// 队列名，默认 {ExchangeName}.{服务名}（服务名取入口程序集名，可用环境变量 LEDON_SERVICE_NAME 覆盖）。
    /// 同一服务的多个实例共用同名队列 = 竞争消费（每条消息只被一个实例处理）。
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>队列绑定交换机的路由键模式，默认 #（本服务收到全部事件，由本地 handler 有无决定是否处理）。</summary>
    public string BindingPattern { get; set; } = "#";

    /// <summary>预取计数（未确认消息上限），默认 20。</summary>
    public ushort PrefetchCount { get; set; } = 20;

    /// <summary>交换机/队列是否持久化，默认 true。</summary>
    public bool Durable { get; set; } = true;

    /// <summary>消息是否持久化，默认 true。</summary>
    public bool PersistentMessages { get; set; } = true;
}
