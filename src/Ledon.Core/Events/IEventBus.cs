namespace Ledon.Core.Events;

/// <summary>
/// 事件总线契约：模块间解耦通信的底层基础设施。
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// 按注册顺序依次调用所有 <see cref="IEventHandler{TEvent}"/> 处理器；任一处理器抛出的异常会直接向上传播。
    /// </summary>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull;
}
