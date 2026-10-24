using System.Text;
using Confluent.Kafka;
using Ledon.Module;
using Ledon.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Queues.Kafka;

/// <summary>
/// Kafka 消费侧：订阅本服务关注的主题，把消息反序列化后派发给进程内
/// <see cref="IIntegrationEventHandler{TEvent}"/>（与发件箱进程内投递同一套分发逻辑）。
/// 同消费组多实例 = 竞争消费；单条处理失败不提交偏移量，由 broker 重新投递（至少一次）——
/// 配合 <c>AddLedonInbox</c> 的收件箱去重即为"恰好一次处理"。
/// </summary>
internal sealed class KafkaConsumerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaConsumerService> _logger;

    public KafkaConsumerService(
        IServiceProvider services,
        KafkaOptions options,
        ILogger<KafkaConsumerService> logger)
    {
        _services = services;
        _options = options;
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
                // 不提交偏移量：broker 重新投递（至少一次语义）
                var failedType = HeaderValue(result.Message.Headers ?? new Headers(), KafkaEventCodec.EventTypeHeader);
                _logger.LogWarning(ex, "集成事件 {Type} 消费失败（等待重新投递）", failedType ?? result.Topic);
                continue;
            }

            if (!_options.EnableAutoCommit)
                consumer.Commit(result);
        }
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
