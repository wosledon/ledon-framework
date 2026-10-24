using Ledon.Core.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Data;

/// <summary>
/// 领域事件分发拦截器：<c>SaveChanges</c> 成功后，把跟踪到的
/// <see cref="IHasDomainEvents"/> 实体上的事件经 <c>IEventBus</c> 分发，然后清空。
/// <para>
/// 语义：事件在数据提交后分发（处理器能看到已提交的数据）；分发失败只记日志不重抛——
/// 数据已提交，重滚不了，且失败不应让调用方以为保存失败。需要可靠投递用发件箱。
/// </para>
/// <para>
/// 重入保护：分发过程中若处理器又触发 SaveChanges，嵌套调用不重复分发
/// （AsyncLocal 守卫，异步安全）。
/// </para>
/// </summary>
internal sealed class DomainEventsDispatcherInterceptor(
    IServiceProvider services,
    Microsoft.Extensions.Logging.ILogger<DomainEventsDispatcherInterceptor> logger)
    : SaveChangesInterceptor
{
    private static readonly AsyncLocal<bool> Dispatching = new();

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await DispatchAsync(eventData.Context).ConfigureAwait(false);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        DispatchAsync(eventData.Context).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    private async Task DispatchAsync(DbContext? context)
    {
        if (context is null || Dispatching.Value)
            return;

        var entities = context.ChangeTracker.Entries()
            .Where(entry => entry.Entity is IHasDomainEvents { DomainEvents.Count: > 0 })
            .Select(entry => (IHasDomainEvents)entry.Entity)
            .ToArray();

        if (entities.Length == 0)
            return;

        // 先从实体摘掉事件再分发：分发期间的新保存不会重复分发同一批
        var events = entities.SelectMany(entity => entity.DomainEvents).ToArray();
        foreach (var entity in entities)
            entity.ClearDomainEvents();

        var eventBus = services.GetService<IEventBus>();

        if (eventBus is null)
        {
            logger.LogWarning("发现 {Count} 个领域事件但未注册 IEventBus（确认引用了 Ledon.Module）", events.Length);
            return;
        }

        Dispatching.Value = true;
        try
        {
            foreach (var @event in events)
            {
                try
                {
                    await eventBus.PublishDynamicAsync(@event, cancellationToken: default).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "领域事件 {Event} 分发失败（数据已提交，不重滚）", @event.GetType().Name);
                }
            }
        }
        finally
        {
            Dispatching.Value = false;
        }
    }
}
