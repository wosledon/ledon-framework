using Ledon;
using Ledon.Module;
using Ledon.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 收件箱入口（消费侧去重）。消息中间件是至少一次投递，handler 幂等不能只靠自觉：
/// <code>
/// builder.Services.AddLedonInbox();
/// // 之后发件箱/broker 投递的集成事件自动按 MessageId 去重：
/// // 重复投递直接跳过，崩溃中断的在认领超时后可重试
/// </code>
/// 存储复用 <see cref="OutboxDbContext"/>（与发件箱同库），无需额外配置。
/// </summary>
public static class LedonInboxServiceCollectionExtensions
{
    public static IServiceCollection AddLedonInbox(
        this IServiceCollection services,
        Action<LedonInboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonInboxOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<IInboxStore, EfInboxStore>();

        // OutboxDbContext 子类自动发现：把具体类型注册为基类型（EfInboxStore 依赖基类型解析）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && type != typeof(OutboxDbContext)
                    && typeof(OutboxDbContext).IsAssignableFrom(type)))
            {
                services.TryAddScoped(typeof(OutboxDbContext), provider => provider.GetRequiredService(type));
            }
        }

        services.AddHostedService<InboxCleanupService>();

        return services;
    }

    /// <summary>收件箱清理：按保留期删除已完成消息（与发件箱清理同节奏，避免重复起调度器）。</summary>
    internal sealed class InboxCleanupService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly LedonInboxOptions _options;
        private readonly ILogger<InboxCleanupService> _logger;

        public InboxCleanupService(
            IServiceProvider services,
            LedonInboxOptions options,
            ILogger<InboxCleanupService> logger)
        {
            _services = services;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_options.CleanupInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _services.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetService<IInboxStore>();
                    if (store is null)
                        return;

                    var deleted = await store.DeleteCompletedAsync(_options.Retention, stoppingToken);
                    if (deleted > 0)
                    {
                        QueueMetrics.InboxCleaned.Add(deleted);
                        _logger.LogInformation("收件箱清理：删除 {Count} 条超保留期的已完成消息", deleted);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "收件箱清理失败（下轮重试）");
                }

                try
                {
                    if (!await timer.WaitForNextTickAsync(stoppingToken))
                        return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
