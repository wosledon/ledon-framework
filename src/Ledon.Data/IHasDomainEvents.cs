namespace Ledon.Data;

/// <summary>
/// 带领域事件的实体标记。实现后，`SaveChangesAsync` 成功时事件由
/// <see cref="DomainEventsDispatcherInterceptor"/> 经 <c>IEventBus</c> 自动分发（进程内）。
/// 跨进程/要求可靠投递时改用发件箱（<c>AddLedonOutbox</c>）——两者可共存。
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>待分发的事件（分发后清空）。</summary>
    IReadOnlyCollection<object> DomainEvents { get; }

    /// <summary>清空事件（分发完成后由拦截器调用）。</summary>
    void ClearDomainEvents();
}

/// <summary>
/// 领域事件收集器的默认实现：实体继承 <see cref="Entity"/> 即获得事件收集能力
/// （也可以自己实现 <see cref="IHasDomainEvents"/>，不必继承）。
/// </summary>
public abstract class Entity : IHasDomainEvents
{
    private readonly List<object> _domainEvents = [];

    public IReadOnlyCollection<object> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>添加一个领域事件。</summary>
    protected void AddDomainEvent(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _domainEvents.Add(@event);
    }
}
