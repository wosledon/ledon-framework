using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using Ledon.Module;
using Ledon.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Ledon.Queues.Kafka;

/// <summary>
/// Kafka 消费侧：订阅本服务关注的主题，把消息反序列化后派发给进程内
/// <see cref="IIntegrationEventHandler{TEvent}"/>（与发件箱进程内投递同一套分发逻辑）。
/// 同消费组多实例 = 竞争消费；单条处理失败不提交偏移量，由 broker 重新投递（至少一次）——
/// 配合 <c>AddLedonInbox</c> 的收件箱去重即为"恰好一次处理"。
/// </summary>
[ExcludeFromCodeCoverage(Justification = "需要真实 broker；由设置 LEDON_TEST_KAFKA_BOOTSTRAP 的集成测试覆盖。")]
internal sealed class KafkaConsumerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly KafkaOptions _options;
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaConsumerService> _logger;

    /// <summary>进程内投递尝试计数（键 = 主题/分区/偏移量）。重启后重置——死信上限是"尽力而为"的护栏。</summary>
    private readonly ConcurrentDictionary<string, int> _attempts = new();

    public KafkaConsumerService(
        IServiceProvider services,
        KafkaOptions options,
        IProducer<string, string> producer,
        ILogger<KafkaConsumerService> logger)
    {
        _services = services;
        _options = options;
        _producer = producer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topics = ResolveTopics();
        if (topics.Count == 0)
        {
            _logger.LogWarning("Kafka 消费侧未订阅任何主题（没有发现集成事件类型，且未配置 ExtraTopics）");
            return;
        }

        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId ?? ServiceName,
            EnableAutoCommit = _options.EnableAutoCommit,
            AutoCommitIntervalMs = (int)_options.AutoCommitInterval.TotalMilliseconds,
            AutoOffsetReset = _options.AutoOffsetReset == AutoOffsetReset.Earliest
                ? Confluent.Kafka.AutoOffsetReset.Earliest
                : Confluent.Kafka.AutoOffsetReset.Latest,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topics);

        _logger.LogInformation(
            "Kafka 消费侧启动：组 {Group} 订阅 [{Topics}]", config.GroupId, string.Join(", ", topics));

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string> result;
            try
            {
                result = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kafka 消费失败（等待重投）");
                continue;
            }

            try
            {
                await HandleAsync(result, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var attempts = _attempts.AddOrUpdate(
                    AttemptKey(result), 1, (_, count) => count + 1);

                if (KafkaDeadLetterRouter.ShouldDeadLetter(attempts, _options.MaxDeliveryAttempts))
                {
                    // 毒消息：转入死信主题并提交偏移量——不再阻塞分区重投
                    await DeadLetterAsync(result, attempts, ex, stoppingToken).ConfigureAwait(false);
                    _attempts.TryRemove(AttemptKey(result), out _);

                    if (!_options.EnableAutoCommit)
                        consumer.Commit(result);

                    continue;
                }

                // 不提交偏移量：broker 重新投递（至少一次语义）
                _logger.LogWarning(ex,
                    "集成事件消费失败（第 {Attempt}/{Max} 次，等待重新投递）: {Topic}[{Partition}]@{Offset}",
                    attempts, _options.MaxDeliveryAttempts, result.Topic, result.Partition, result.Offset);
                continue;
            }

            if (!_options.EnableAutoCommit)
                consumer.Commit(result);
        }
    }

    private static string AttemptKey(ConsumeResult<string, string> result) =>
        $"{result.Topic}/{result.Partition.Value}/{result.Offset.Value}";

    private async Task DeadLetterAsync(
        ConsumeResult<string, string> result,
        int attempts,
        Exception error,
        CancellationToken cancellationToken)
    {
        var deadLetterTopic = KafkaDeadLetterRouter.DeadLetterTopicFor(result.Topic, _options.DeadLetterTopicSuffix);

        _logger.LogError(error,
            "集成事件消费失败达 {Attempts} 次上限，转入死信主题 {DeadLetterTopic}（原消息保留，可重置消费组偏移量重放）",
            attempts, deadLetterTopic);

        var message = KafkaDeadLetterRouter.BuildDeadLetterMessage(
            result.Message, attempts, error.Message);

        await _producer.ProduceAsync(deadLetterTopic, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var headers = result.Message.Headers ?? new Headers();

        var eventTypeName = HeaderValue(headers, KafkaEventCodec.EventTypeHeader)
            ?? throw new InvalidOperationException("消息缺少 ledon-event-type 头，无法反序列化");
        var messageId = HeaderValue(headers, KafkaEventCodec.MessageIdHeader);

        var @event = KafkaEventCodec.Deserialize(eventTypeName, result.Message.Value);

        await using var scope = _services.CreateAsyncScope();
        await IntegrationEventDispatcher.DispatchAsync(scope.ServiceProvider, @event, messageId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? HeaderValue(Headers headers, string key)
    {
        var header = headers.FirstOrDefault(candidate => candidate.Key == key);
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }

    /// <summary>订阅主题：扫描到的集成事件类型对应主题 + 额外配置的主题。</summary>
    private List<string> ResolveTopics()
    {
        var topics = new List<string>();

        if (_options.SubscribeDiscoveredEventTypes)
        {
            foreach (var assembly in LedonAssemblies.Scan())
            {
                foreach (var type in assembly.GetLoadableTypes()
                    .Where(type => type is { IsClass: true, IsAbstract: false }
                        && typeof(IIntegrationEvent).IsAssignableFrom(type)))
                {
                    topics.Add(KafkaEventCodec.TopicFor(type, _options.TopicPrefix));
                }
            }
        }

        topics.AddRange(_options.ExtraTopics);

        return topics.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>服务名（默认消费组后缀）：环境变量 LEDON_SERVICE_NAME 优先，其次入口程序集名。</summary>
    private static string ServiceName =>
        Environment.GetEnvironmentVariable("LEDON_SERVICE_NAME")
        ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
        ?? "ledon-app";
}



