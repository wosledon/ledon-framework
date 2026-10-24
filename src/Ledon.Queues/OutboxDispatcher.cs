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
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.PollingInterval, stoppingToken);

            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发件箱调度批次失败（下轮重试）");
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var pending = await store.GetPendingAsync(_options.BatchSize, cancellationToken);
        if (pending.Count == 0)
            return;

        foreach (var message in pending)
        {
            using var activity = LedonActivity.Modules.StartActivity("Outbox.Dispatch");
            activity?.SetTag("event.type", message.Type);

            try
            {
                await DeliverAsync(scope.ServiceProvider, message, cancellationToken);
                await store.MarkProcessedAsync(message.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex, "集成事件 {Type} 投递失败（第 {Retry} 次）", message.Type, message.RetryCount + 1);
                await store.MarkFailedAsync(message.Id, ex.Message, cancellationToken);
            }
        }
    }

    /// <summary>反射分发：事件类型运行时才知道（JSON 里只有类型全名）。</summary>
    private static async Task DeliverAsync(IServiceProvider services, OutboxMessage message, CancellationToken cancellationToken)
    {
        var eventType = Type.GetType(message.Type)
            ?? throw new InvalidOperationException($"无法加载事件类型 {message.Type}");

        var @event = JsonSerializer.Deserialize(message.Payload, eventType, SerializerOptions)
            ?? throw new InvalidOperationException($"事件 {message.Type} 载荷反序列化为 null");

        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(handlerType);

        var handlers = (System.Collections.IEnumerable?)services.GetService(enumerableType);
        if (handlers is null)
            return;

        var method = handlerType.GetMethod(nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync))!;

        foreach (var handler in handlers)
        {
            var task = (Task)method.Invoke(handler, [@event, cancellationToken])!;
            await task;
        }
    }
}
