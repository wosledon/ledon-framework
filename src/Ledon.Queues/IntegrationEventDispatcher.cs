namespace Ledon.Queues;

/// <summary>
/// 集成事件 → 本地 handler 的反射分发：发件箱调度器（进程内投递）与
/// broker 消费侧（<c>Ledon.Queues.RabbitMQ</c>）共用同一套分发逻辑。
/// 自写 broker 消费侧时也可直接复用。
/// </summary>
public static class IntegrationEventDispatcher
{
    public static async Task DispatchAsync(
        IServiceProvider services,
        IIntegrationEvent @event,
        CancellationToken cancellationToken)
    {
        var eventType = @event.GetType();
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
