namespace Ledon.Queues;

/// <summary>
/// 集成事件标记：跨模块/跨进程的领域事件，经 Outbox 保证至少一次投递。
/// 与进程内事件（<c>IEventHandler&lt;T&gt;</c>，Ledon.Core）区分：
/// 进程内事件追求即时，集成事件追求可靠。
/// </summary>
public interface IIntegrationEvent;

/// <summary>集成事件处理器。实现类被自动发现并注册。</summary>
public interface IIntegrationEventHandler<in TEvent>
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}
