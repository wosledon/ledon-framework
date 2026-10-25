using Ledon;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Module.Diagnostics;
using Ledon.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 持久化后台队列入口：任务落库（宿主重启后仍在），由调度器按轮询投递给处理器。
/// <code>
/// builder.Services.AddLedonDurableQueue();
///
/// // 业务代码：与业务数据同事务入队（调用方负责 SaveChanges）
/// await durableQueue.EnqueueAsync(new DurableJob("send-email", json));
///
/// // 处理器（实现类自动发现）
/// public class SendEmailHandler : IDurableJobHandler
/// {
///     public string Name =&gt; "send-email";
///     public Task ExecuteAsync(string payload, CancellationToken ct) =&gt; /* ... */;
/// }
/// </code>
/// </summary>
public static class LedonDurableQueueServiceCollectionExtensions
{
    public static IServiceCollection AddLedonDurableQueue(
        this IServiceCollection services,
        Action<LedonDurableQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonDurableQueueOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<IDurableQueue, EfDurableQueue>();

        // OutboxDbContext 子类自动发现（持久化队列与发件箱同库）。
        // 多个候选时取"第一个能解析的"——测试场景可能只注册了其中一个
        var durableContexts = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type != typeof(OutboxDbContext)
                && typeof(OutboxDbContext).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        services.TryAddScoped(typeof(OutboxDbContext), provider =>
            durableContexts.Select(type => provider.GetService(type) as DbContext).FirstOrDefault(context => context is not null)
            ?? throw new InvalidOperationException(
                $"未注册任何 OutboxDbContext 子类（候选：{string.Join(", ", durableContexts.Select(t => t.Name))}）。" +
                "请 AddDbContext 注册其中一个，或自行注册 IDurableQueue。"));

        // 处理器自动发现（按名字索引；[DisableAutoDiscovery] 的类型不参与——手动注册优先）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IDurableJobHandler).IsAssignableFrom(type)
                    && !type.IsDefined(typeof(DisableAutoDiscoveryAttribute), inherit: false)))
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IDurableJobHandler), type));
            }
        }

        services.AddHostedService<DurableBackgroundQueueDispatcher>();

        return services;
    }
}

/// <summary>
/// 持久化队列调度器：按轮询把待处理任务投递给处理器（至少一次语义，
/// 与发件箱一致的失败重试/搁置策略）。
/// </summary>
internal sealed class DurableBackgroundQueueDispatcher : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly LedonDurableQueueOptions _options;
    private readonly ILogger<DurableBackgroundQueueDispatcher> _logger;

    public DurableBackgroundQueueDispatcher(
        IServiceProvider services,
        LedonDurableQueueOptions options,
        ILogger<DurableBackgroundQueueDispatcher> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "持久化队列调度批次失败（下轮重试）");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetService<IDurableQueue>();
        if (queue is null)
            return;

        List<QueuedJobMessage> jobs;
        try
        {
            jobs = (await queue.DequeueAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex)
        {
            // 存储未就绪（schema 未建/提供程序短暂不可用）：不炸宿主，下轮重试。
            // schema 由 AddLedonData 的前置建库负责，本服务只消费
            _logger.LogDebug(ex, "持久化队列取批失败（schema 未就绪或存储不可用，下轮重试）");
            return;
        }

        foreach (var job in jobs)
        {
            using var activity = LedonActivity.Queues.StartActivity("DurableQueue.Process");
            activity?.SetTag("queue.handler", job.QueueName);

            if (job.RetryCount >= _options.MaxRetries)
            {
                // 搁置（保留现场可人工处理）：记 Warning 但不抛，避免阻塞后续
                _logger.LogWarning(
                    "持久化任务 {Handler}({Id}) 重试已达上限 {Max}，搁置",
                    job.QueueName, job.Id, _options.MaxRetries);
                continue;
            }

            try
            {
                await using var handlerScope = _services.CreateAsyncScope();
                var handler = ResolveHandler(handlerScope.ServiceProvider, job.QueueName);

                if (handler is null)
                {
                    _logger.LogError("持久化任务 {Handler} 没有对应处理器（IDurableJobHandler 实现）", job.QueueName);
                    await queue.MarkFailedAsync(job.Id, "未找到处理器", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await handler.ExecuteAsync(job.Payload, cancellationToken).ConfigureAwait(false);
                await queue.MarkProcessedAsync(job.Id, cancellationToken).ConfigureAwait(false);
                QueueMetrics.QueueProcessed.Add(1);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex,
                    "持久化任务 {Handler}({Id}) 执行失败（第 {Retry} 次）",
                    job.QueueName, job.Id, job.RetryCount + 1);
                await queue.MarkFailedAsync(job.Id, ex.Message, cancellationToken).ConfigureAwait(false);
                QueueMetrics.QueueFailed.Add(1);
            }
        }

        // 顺手清理（ Retention > 0 时才有效，开销是一次 DELETE）
        if (_options.Retention > TimeSpan.Zero)
            await queue.DeleteProcessedAsync(_options.Retention, cancellationToken).ConfigureAwait(false);
    }

    private static IDurableJobHandler? ResolveHandler(IServiceProvider services, string handlerName) =>
        services.GetServices<IDurableJobHandler>()
            .FirstOrDefault(handler => string.Equals(handler.Name, handlerName, StringComparison.Ordinal));
}
