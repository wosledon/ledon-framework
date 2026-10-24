using System.Diagnostics.CodeAnalysis;
using Ledon;
using Ledon.Module;
using Ledon.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 工作流入口。日常用法一行：
/// <code>
/// builder.Services.AddLedonWorkflow();
///
/// // 业务代码：创建实例 → 运行（或由恢复服务在重启后自动继续）
/// var engine = provider.GetRequiredService&lt;WorkflowEngine&gt;();
/// var instance = await engine.CreateAsync("order", new { OrderId = 1 });
/// await engine.RunAsync(instance.Id, ct);
/// </code>
/// 步骤与定义实现类被自动发现（引用框架 = 选择加入），零注册。
/// </summary>
public static class LedonWorkflowServiceCollectionExtensions
{
    /// <summary>反射边界说明（裁剪/NativeAOT 警告文案）。</summary>
    private const string AotBoundary =
        "Ledon 的步骤/定义自动发现基于反射（Assembly.GetTypes）。" +
        "NativeAOT 或裁剪发布时，相关类型必须可在运行时解析（rd.xml / DynamicDependency 保留）。";

    [RequiresDynamicCode(AotBoundary)]
    public static IServiceCollection AddLedonWorkflow(
        this IServiceCollection services,
        Action<WorkflowOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 复用模块系统的扫描与生命周期（幂等）
        services.AddLedon();

        var options = new WorkflowOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<WorkflowEngine>();

        // 持久化：WorkflowDbContext 子类自动注册为基类型（EfWorkflowStore 依赖基类型解析）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && type != typeof(WorkflowDbContext)
                    && typeof(WorkflowDbContext).IsAssignableFrom(type)))
            {
                services.TryAddScoped(typeof(WorkflowDbContext), provider => provider.GetRequiredService(type));
            }
        }

        // 有存储才注册恢复服务与 EF 存储实现
        services.TryAddScoped<IWorkflowStore, EfWorkflowStore>();

        // 步骤与定义自动发现（引用框架 = 选择加入；手动注册优先）
        foreach (var assembly in LedonAssemblies.Scan())
        {
            foreach (var type in assembly.GetLoadableTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false })
                .Where(type => typeof(IWorkflowStep).IsAssignableFrom(type)
                    || typeof(IWorkflowDefinition).IsAssignableFrom(type)))
            {
                if (typeof(IWorkflowStep).IsAssignableFrom(type))
                    services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IWorkflowStep), type));

                if (typeof(IWorkflowDefinition).IsAssignableFrom(type))
                    services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IWorkflowDefinition), type));
            }
        }

        // 进程重启后恢复：Running 实例由托管服务继续推进（有界批量）
        services.AddHostedService<WorkflowRecoveryService>();

        return services;
    }
}

/// <summary>
/// 工作流恢复服务：宿主启动时把存储里 Running 状态的实例继续推进
/// （进程崩溃/部署中断的工作流不丢），之后按轮询间隔持续恢复。
/// </summary>
internal sealed class WorkflowRecoveryService(
    IServiceProvider services,
    Microsoft.Extensions.Logging.ILogger<WorkflowRecoveryService> logger)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动即恢复一次（崩溃恢复的主路径）
        await RecoverAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(PollingInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await RecoverAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "工作流恢复失败（下轮重试）");
            }
        }
    }
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var store = scope.ServiceProvider.GetService<IWorkflowStore>();
        if (store is null)
            return;   // 未配存储：引擎仍可用（进程内），恢复无意义

        List<WorkflowInstance> resumable;
        try
        {
            resumable = (await store.GetResumableAsync(BatchSize, cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex)
        {
            // 存储未就绪（表还没建/提供程序短暂不可用）：不炸宿主，下轮再试。
            // schema 由 AddLedonData 的前置建库负责，本服务只消费
            logger.LogDebug(ex, "工作流恢复查询失败（schema 未就绪或存储不可用，下轮重试）");
            return;
        }

        if (resumable.Count == 0)
            return;

        logger.LogInformation("工作流恢复：继续推进 {Count} 个运行中实例", resumable.Count);

        foreach (var instance in resumable)
        {
            try
            {
                var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
                await engine.RunAsync(instance.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个实例失败不影响其余；下次轮询/重启后再试
                logger.LogWarning(ex, "工作流实例 {Instance} 恢复失败（稍后重试）", instance.Id);
            }
        }
    }
}
