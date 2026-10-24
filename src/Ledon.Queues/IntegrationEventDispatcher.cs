using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Queues;

/// <summary>
/// 集成事件 → 本地 handler 的反射分发：发件箱调度器（进程内投递）与
/// broker 消费侧（<c>Ledon.Queues.RabbitMQ</c>）共用同一套分发逻辑。
/// 自写 broker 消费侧时也可直接复用。
/// 注册了 <see cref="IInboxStore"/> 且传入 <paramref name="messageId"/> 时，按消息标识去重
/// （至少一次投递 + 恰好一次处理）；未注册存储时保持原语义。
/// </summary>
public static class IntegrationEventDispatcher
{
    public static Task DispatchAsync(
        IServiceProvider services,
        IIntegrationEvent @event,
        CancellationToken cancellationToken)
        => DispatchAsync(services, @event, messageId: null, cancellationToken);

    public static async Task DispatchAsync(
        IServiceProvider services,
        IIntegrationEvent @event,
        string? messageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(@event);

        // 收件箱去重：已处理完成（或正在被处理）的重复投递直接跳过
        var inbox = services.GetService<IInboxStore>();
        var dedupe = inbox is not null && !string.IsNullOrEmpty(messageId);
        if (dedupe)
        {
            var eventType = @event.GetType().AssemblyQualifiedName ?? @event.GetType().FullName!;
            if (!await inbox!.TryClaimAsync(messageId!, eventType, cancellationToken).ConfigureAwait(false))
            {
                QueueMetrics.InboxSkipped.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
                return;
            }
        }

        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(@event.GetType());
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(handlerType);

        var handlers = (System.Collections.IEnumerable?)services.GetService(enumerableType);
        if (handlers is not null)
        {
            var method = handlerType.GetMethod(nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync))!;

            foreach (var handler in handlers)
            {
                var task = (Task)method.Invoke(handler, [@event, cancellationToken])!;
                await task.ConfigureAwait(false);
            }
        }

        if (dedupe)
            await inbox!.MarkCompletedAsync(messageId!, cancellationToken).ConfigureAwait(false);
    }
}
