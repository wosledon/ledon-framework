using Ledon.Core.Events;
using Ledon.Data;
using Ledon.Queues;

namespace Ledon.Transactions;

/// <summary>
/// 统一事务入口。用户只写一个 <see cref="ExecuteAsync"/>，不用关心：
/// <list type="bullet">
///   <item>本地事务——自动 BeginTransaction / SaveChanges / Commit，异常自动 Rollback；</item>
///   <item>领域事件——提交成功后才分发（不会出现"事件发了但事务回滚了"）；</item>
///   <item>集成事件——<see cref="AddIntegrationEvent"/> 配了发件箱就与业务数据原子落库（可靠投递），
///         没配就退化为提交后的进程内分发。**同一种写法，两种部署形态。**</item>
/// </list>
/// <code>
/// public class OrderService(IUnitOfWork uow)
/// {
///     public async Task CreateAsync(CreateOrder cmd, CancellationToken ct)
///     {
///         await uow.ExecuteAsync(async token =>
///         {
///             var order = new Order(cmd);
///             uow.AddIntegrationEvent(new OrderPlaced(order.Id));   // 原子 + 可靠
///             await uow.SaveChangesAsync(token);
///         }, ct);
///     }
/// }
/// </code>
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>当前是否有活动事务（嵌套 <see cref="ExecuteAsync"/> 会加入外层事务）。</summary>
    bool HasActiveTransaction { get; }

    /// <summary>本次事务收集到的领域事件（提交后分发）。</summary>
    IReadOnlyCollection<object> DomainEvents { get; }

    /// <summary>
    /// 在事务中执行业务逻辑：开启事务 → 执行 → 保存 → 提交 → 分发领域事件。
    /// 异常时自动回滚并原样抛出；已提交的事件不回滚（事件在提交后才分发）。
    /// 嵌套调用加入外层事务（内层不单独提交）。
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);

    /// <summary><see cref="ExecuteAsync{T}"/> 的无返回值重载。</summary>
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);

    /// <summary>保存变更（幂等；未开启事务时等价于 DbContext.SaveChangesAsync）。</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>提交事务（幂等）。未开启事务时无操作。</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>回滚事务（幂等）。未开启事务时无操作。</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加集成事件（跨模块/跨服务）。
    /// 注册了发件箱（<c>AddLedonOutbox</c>）→ 与业务数据同事务落库，由调度器可靠投递（可到 broker）；
    /// 未注册 → 提交后经 <c>IEventBus</c> 进程内分发。调用方无需区分。
    /// </summary>
    void AddIntegrationEvent(IIntegrationEvent @event);

    /// <summary>注册领域事件（实体未实现 <see cref="IHasDomainEvents"/> 时手动用）。</summary>
    void AddDomainEvent(object @event);
}
