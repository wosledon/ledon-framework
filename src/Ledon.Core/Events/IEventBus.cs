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

    /// <summary>
    /// 动态分发（编译期不知道事件类型时用，如领域事件拦截器）：
    /// 内部按运行时类型解析 <see cref="IEventHandler{TEvent}"/> 后转发到
    /// <see cref="PublishAsync{TEvent}"/>——语义（顺序、错误策略）完全一致。
    /// </summary>
    Task PublishDynamicAsync(object @event, CancellationToken cancellationToken = default);
}
