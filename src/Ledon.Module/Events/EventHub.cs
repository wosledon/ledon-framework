using Ledon.Core.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Events;

/// <summary>
/// 事件总线默认实现：从容器解析该事件的所有处理器，按注册顺序依次执行。
/// 处理器异常会中断后续处理器并直接向上传播（失败即停，与任务编排语义一致）。
/// </summary>
internal sealed class EventHub : IEventBus
{
    private readonly IServiceProvider _services;

    public EventHub(IServiceProvider services)
    {
        _services = services;
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);

        foreach (var handler in _services.GetServices<IEventHandler<TEvent>>())
        {
            await handler.HandleAsync(@event, cancellationToken).ConfigureAwait(false);
        }
    }
}
