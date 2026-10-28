using System.Text;
using System.Text.Json;
using Ledon.Queues;
using global::RabbitMQ.Client;
using global::RabbitMQ.Client.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Ledon.Queues.RabbitMQ;

/// <summary>
/// RabbitMQ 消费侧：订阅本服务队列，把消息反序列化后派发给进程内
/// <see cref="IIntegrationEventHandler{TEvent}"/>（与发件箱进程内投递同一套分发逻辑）。
/// 同队列多实例 = 竞争消费；单条处理失败只记日志（不 confirm），由 broker 重新投递（至少一次）。
/// </summary>
[ExcludeFromCodeCoverage(Justification = "需要真实 broker；由设置 LEDON_TEST_RABBITMQ_URL 的集成测试覆盖。")]
internal sealed class RabbitMqConsumerService : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IServiceProvider _services;
    private readonly IConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConsumerService> _logger;

    public RabbitMqConsumerService(
        IServiceProvider services,
        IConnection connection,
        RabbitMqOptions options,
        ILogger<RabbitMqConsumerService> logger)
    {
        _services = services;
        _connection = connection;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Topic,
            durable: _options.Durable,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        var queue = _options.QueueName ?? $"{_options.ExchangeName}.{ServiceName}";
        await channel.QueueDeclareAsync(
            queue: queue,
            durable: _options.Durable,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        await channel.QueueBindAsync(
            queue: queue,
            exchange: _options.ExchangeName,
            routingKey: _options.BindingPattern,
            arguments: null,
            cancellationToken: stoppingToken);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "RabbitMQ 消费侧启动：队列 {Queue} 绑定 {Exchange}（{Pattern}）", queue, _options.ExchangeName, _options.BindingPattern);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                await HandleAsync(args, stoppingToken).ConfigureAwait(false);
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // 不 confirm：broker 重新投递给其它实例/本实例（至少一次语义）
                _logger.LogWarning(ex, "集成事件 {Type} 消费失败（等待重新投递）", args.BasicProperties.Type);
            }
        };

        await channel.BasicConsumeAsync(
            queue: queue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        // 保持服务存活直到宿主持停（消费在事件回调里进行）
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        var typeName = args.BasicProperties.Type;
        if (string.IsNullOrEmpty(typeName))
            throw new InvalidOperationException("消息缺少 Type 属性，无法反序列化");

        var eventType = Type.GetType(typeName)
            ?? throw new InvalidOperationException($"无法加载事件类型 {typeName}");

        var payload = Encoding.UTF8.GetString(args.Body.ToArray());
        var boxed = JsonSerializer.Deserialize(payload, eventType, SerializerOptions)
            ?? throw new InvalidOperationException($"事件 {typeName} 载荷反序列化为 null");

        var @event = (IIntegrationEvent)boxed;

        await using var scope = _services.CreateAsyncScope();
        // MessageId 透传：收件箱去重凭据（broker 重投时跳过已处理的消息）
        await IntegrationEventDispatcher.DispatchAsync(scope.ServiceProvider, @event, args.BasicProperties.MessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>服务名（队列名默认后缀）：环境变量 LEDON_SERVICE_NAME 优先，其次入口程序集名。</summary>
    private static string ServiceName =>
        Environment.GetEnvironmentVariable("LEDON_SERVICE_NAME")
        ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
        ?? "ledon-app";
}



