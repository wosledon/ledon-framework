using System.Data.Common;
using Ledon.Core.Events;
using Ledon.Data;
using Ledon.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Transactions;

/// <summary>
/// <see cref="IUnitOfWork"/> 的 EF 实现：
/// 一个作用域一个实例；事务生命周期由 <see cref="ExecuteAsync"/> 统一管理。
/// </summary>
internal sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly IServiceProvider _services;
    private readonly LedonTransactionOptions _options;
    private readonly ILogger<EfUnitOfWork> _logger;

    private DbContext? _context;
    private DbContext? Context => _context ??= ResolveContext();

    private IDbContextTransaction? _transaction;
    private readonly List<object> _domainEvents = [];
    private bool _committed;
    private bool _completed;

    public EfUnitOfWork(
        IServiceProvider services,
        LedonTransactionOptions options,
        ILogger<EfUnitOfWork> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    public bool HasActiveTransaction => _transaction is not null;

    public IReadOnlyCollection<object> DomainEvents => _domainEvents;

    public void AddDomainEvent(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _domainEvents.Add(@event);
    }

    public void AddIntegrationEvent(IIntegrationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // 有发件箱：与业务数据同事务（原子）；无发件箱：提交后进程内分发
        var outbox = _services.GetService<IOutboxStore>();
        if (outbox is not null)
        {
            outbox.Add(@event);
            return;
        }

        AddDomainEvent(@event);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 重入：外层已开事务时内层直接执行（共享同一事务，内层不单独提交）
        if (_completed)
            throw new InvalidOperationException("IUnitOfWork 已完成（ExecuteAsync 不可重入）；同一作用域请只调用一次或改用嵌套 ExecuteAsync。");

        if (_transaction is not null)
            return await action(cancellationToken).ConfigureAwait(false);

        var context = Context ?? throw new InvalidOperationException(
            "未找到已注册的 DbContext。请先 AddDbContext（或 AddLedonData 自动发现），" +
            "多个 DbContext 时用 AddLedonTransactions 的 configureDbContext 指定主上下文。");

        await BeginTransactionAsync(context, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await CommitTransactionAsync(cancellationToken).ConfigureAwait(false);

            await DispatchDomainEventsAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;

            return result;
        }
        catch
        {
            await RollbackTransactionAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        return ExecuteAsync<object?>(async token =>
        {
            await action(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var context = Context ?? throw new InvalidOperationException("未找到已注册的 DbContext。");

        // 实体上的领域事件先收集（SaveChanges 会触发拦截器分发，这里抢先收走，
        // 保证事件在事务提交后才分发——提交与分发之间不会出现"回滚了但事件已发"）
        CollectEntityEvents(context);

        return await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null || _committed)
            return;

        var context = Context!;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;

        await DispatchDomainEventsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        await RollbackTransactionAsync().ConfigureAwait(false);
    }

    private async Task BeginTransactionAsync(DbContext context, CancellationToken cancellationToken)
    {
        _transaction = _options.IsolationLevel is { } level
            ? await context.Database.BeginTransactionAsync(level, cancellationToken).ConfigureAwait(false)
            : await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitTransactionAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null || _committed)
            return;

        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    private async Task RollbackTransactionAsync()
    {
        if (_transaction is null || _committed)
            return;

        try
        {
            await _transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 回滚失败不掩盖原始异常（业务异常更重要）
            _logger.LogWarning(ex, "事务回滚失败（原始异常将继续抛出）");
        }
    }

    /// <summary>从变更跟踪器收集实体上的领域事件并清空（避免拦截器重复分发）。</summary>
    private void CollectEntityEvents(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IHasDomainEvents { DomainEvents.Count: > 0 } entity)
                continue;

            _domainEvents.AddRange(entity.DomainEvents);
            entity.ClearDomainEvents();
        }
    }

    private async Task DispatchDomainEventsAsync(CancellationToken cancellationToken)
    {
        if (_domainEvents.Count == 0)
            return;

        if (!_options.DispatchDomainEventsAfterCommit)
            return;

        var eventBus = _services.GetService<IEventBus>();
        if (eventBus is null)
        {
            _logger.LogWarning("提交后有 {Count} 个领域事件待分发，但未注册 IEventBus（确认引用了 Ledon.Module）", _domainEvents.Count);
            return;
        }

        foreach (var @event in _domainEvents)
        {
            try
            {
                await eventBus.PublishDynamicAsync(@event, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 提交后分发失败不回滚（数据已提交）：记录并继续其余事件
                _logger.LogError(ex, "领域事件 {Event} 提交后分发失败（数据已提交，不回滚）", @event.GetType().Name);
            }
        }
    }

    private DbContext? ResolveContext()
    {
        // 优先用户在 AddLedonTransactions 指定的选择器
        if (_services.GetService<Func<IServiceProvider, DbContext>?>() is { } selector)
            return selector(_services);

        // 否则取第一个能解析的 DbContext（注册顺序即发现顺序）
        foreach (var type in UnitOfWorkRegistry.CandidateContextTypes)
        {
            if (_services.GetService(type) is DbContext context)
                return context;
        }

        return null;
    }
    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }
}
