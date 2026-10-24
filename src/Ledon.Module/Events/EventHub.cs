using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Ledon.Core.Events;
using Ledon.Module.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Module.Events;

/// <summary>
/// 事件总线默认实现：从容器解析该事件的所有处理器，按注册顺序依次执行。
/// 每次发布产生一个 span（event 类型作为 tag）——"谁在什么时候发布了什么"可观测。
/// 处理器失败时的行为见 <see cref="EventOptions.HandlerErrorHandling"/>（默认失败即停）。
/// </summary>
internal sealed class EventHub : IEventBus
{
    private readonly IServiceProvider _services;
    private readonly EventOptions _options;

    public EventHub(IServiceProvider services, EventOptions options)
    {
        _services = services;
        _options = options;
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);

        using var activity = LedonActivity.Events.StartActivity("Event.Publish");
        activity?.SetTag("event.type", typeof(TEvent).FullName);

        var logger = _services.GetService<ILoggerFactory>()?.CreateLogger("Ledon.Module.Events");
        var isolate = _options.HandlerErrorHandling == EventHandlerErrorHandling.Isolate;
        Exception? firstFailure = null;

        foreach (var handler in _services.GetServices<IEventHandler<TEvent>>())
        {
            if (!isolate)
            {
                await handler.HandleAsync(@event, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 隔离模式：单个 handler 失败只记录，不阻断其余订阅方
            try
            {
                await handler.HandleAsync(@event, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                logger?.LogError(ex,
                    "事件处理器 {Handler} 处理 {Event} 失败（已隔离，继续执行其余处理器）",
                    handler.GetType().Name, typeof(TEvent).Name);
            }
        }

        // 隔离不静默：全部处理器执行完后，把首个失败原样抛给发布方
        if (firstFailure is not null)
            ExceptionDispatchInfo.Throw(firstFailure);
    }
}

