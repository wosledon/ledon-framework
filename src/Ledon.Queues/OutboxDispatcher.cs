using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Ledon.Module.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Queues;

/// <summary>
/// 发件箱调度器：按轮询间隔把待投递消息投给集成事件处理器（至少一次语义）。
/// 单条失败只记重试，不阻塞后续消息；超重试上限的消息搁置（保留现场可人工处理）。
/// 注册了 <see cref="IIntegrationEventTransport"/> 时投给传输（消息中间件）而非进程内 handler。
/// 另按 <see cref="LedonOutboxOptions.CleanupInterval"/> 做两项维护：清理超保留期的已处理消息、
/// 报告搁置（死信）数量——死信不会自愈，需经 <see cref="IOutboxStore.ReviveAsync"/> 人工重放。
/// </summary>
internal sealed class OutboxDispatcher : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IServiceProvider _services;
    private readonly LedonOutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceProvider services,
        LedonOutboxOptions options,
        ILogger<OutboxDispatcher> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextMaintenance = DateTime.UtcNow + _options.CleanupInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);

            try
            {
                await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发件箱调度批次失败（下轮重试）");
            }

            if (DateTime.UtcNow < nextMaintenance)
                continue;

            nextMaintenance = DateTime.UtcNow + _options.CleanupInterval;

            try
            {
                await MaintainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发件箱维护失败（下轮重试）");
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var pending = await store.GetPendingAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
            return;

        foreach (var message in pending)
        {
            using var activity = LedonActivity.Modules.StartActivity("Outbox.Dispatch");
            activity?.SetTag("event.type", message.Type);

            try
            {
                await DeliverAsync(scope.ServiceProvider, message, cancellationToken).ConfigureAwait(false);
                await store.MarkProcessedAsync(message.Id, cancellationToken).ConfigureAwait(false);
                QueueMetrics.OutboxDispatched.Add(1, new KeyValuePair<string, object?>("event.type", message.Type));
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex, "集成事件 {Type} 投递失败（第 {Retry} 次）", message.Type, message.RetryCount + 1);
                await store.MarkFailedAsync(message.Id, ex.Message, cancellationToken).ConfigureAwait(false);
                QueueMetrics.OutboxRetried.Add(1, new KeyValuePair<string, object?>("event.type", message.Type));
            }
        }
    }

    /// <summary>维护：清理超保留期的已处理消息；死信数量告警（搁置消息不会自愈，需人工重放）。</summary>
    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var deleted = await store.DeleteProcessedAsync(_options.Retention, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            QueueMetrics.OutboxCleaned.Add(deleted);
            _logger.LogInformation(
                "发件箱清理：删除 {Count} 条超保留期（{Retention}）的已处理消息", deleted, _options.Retention);
        }

        var deadLetters = await store.GetDeadLettersAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false);
        if (deadLetters.Count == 0)
            return;

        QueueMetrics.OutboxDeadLetters.Add(deadLetters.Count);
        _logger.LogWarning(
            "发件箱有 {Count} 条消息搁置（重试已达上限 {MaxRetries}，示例：{Sample}）。排查原因后经 IOutboxStore.ReviveAsync 重放",
            deadLetters.Count,
            _options.MaxRetries,
            string.Join(", ", deadLetters.Select(message => $"{message.Type}({message.Id})")));
    }

    /// <summary>反射分发：事件类型运行时才知道（JSON 里只有类型全名）。</summary>
    private static async Task DeliverAsync(IServiceProvider services, OutboxMessage message, CancellationToken cancellationToken)
    {
        var eventType = Type.GetType(message.Type)
            ?? throw new InvalidOperationException($"无法加载事件类型 {message.Type}");

        var boxed = JsonSerializer.Deserialize(message.Payload, eventType, SerializerOptions)
            ?? throw new InvalidOperationException($"事件 {message.Type} 载荷反序列化为 null");

        var @event = (IIntegrationEvent)boxed;
        var messageId = message.Id.ToString();

        // 注册了传输（消息中间件）：投给 broker，由消费侧派发——跨服务解耦
        var transport = services.GetService<IIntegrationEventTransport>();
        if (transport is not null)
        {
            await transport.PublishAsync(@event, messageId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await IntegrationEventDispatcher.DispatchAsync(services, @event, messageId, cancellationToken).ConfigureAwait(false);
    }
}
