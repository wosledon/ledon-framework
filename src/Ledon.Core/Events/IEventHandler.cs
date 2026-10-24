namespace Ledon.Core.Events;

/// <summary>
/// 事件处理器契约。一个事件可有多个处理器，按其在容器中的注册顺序依次执行。
/// </summary>
/// <typeparam name="TEvent">要处理的事件类型（按精确类型匹配，不支持基类协变查找）。</typeparam>
public interface IEventHandler<in TEvent>
{
    /// <summary>处理事件。</summary>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}
