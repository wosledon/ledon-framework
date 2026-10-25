using System.Diagnostics.CodeAnalysis;
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
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的自动发现与约定注入基于反射（Assembly.GetTypes / Type.GetType 反序列化）。" +
        "NativeAOT 或裁剪发布时，集成事件类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonInbox(
        this IServiceCollection services,
        Action<LedonInboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonInboxOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<IInboxStore, EfInboxStore>();

        // OutboxDbContext 子类自动发现：把具体类型注册为基类型（EfInboxStore 依赖基类型解析）。
        // 多个候选时取"第一个能解析的"——测试场景可能只注册了其中一个
        var inboxContexts = LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type != typeof(OutboxDbContext)
                && typeof(OutboxDbContext).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        services.TryAddScoped(typeof(OutboxDbContext), provider =>
            inboxContexts.Select(type => provider.GetService(type) as DbContext).FirstOrDefault(context => context is not null)
            ?? throw new InvalidOperationException(
                $"未注册任何 OutboxDbContext 子类（候选：{string.Join(", ", inboxContexts.Select(t => t.Name))}）。" +
                "请 AddDbContext 注册其中一个，或自行注册 IInboxStore。"));

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

                    var deleted = await store.DeleteCompletedAsync(_options.Retention, stoppingToken).ConfigureAwait(false);
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
