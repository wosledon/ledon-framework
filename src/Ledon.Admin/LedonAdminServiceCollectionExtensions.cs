using Ledon.Admin;
using Ledon.Queues;
using Ledon.Scheduling;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 统一运维端点入口（只读）：
/// <code>
/// builder.Services.AddLedonAdmin();   // 默认路径前缀 /admin/ops
/// </code>
/// 端点只暴露状态与统计，不做任何写操作（重放/清理等运维动作由各自能力包的 API 显式调用）。
/// </summary>
public static class LedonAdminServiceCollectionExtensions
{
    /// <summary>注册运维端点（幂等）。</summary>
    public static IServiceCollection AddLedonAdmin(this IServiceCollection services)
        => services.AddLedonAdmin(_ => { });

    /// <summary>注册运维端点并配置。</summary>
    public static IServiceCollection AddLedonAdmin(
        this IServiceCollection services,
        Action<LedonAdminOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LedonAdminOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(_ => new QueueMetricsReader().Start());
        services.TryAddSingleton<OpsSnapshotService>();

        // 框架包自身不进约定扫描，端点显式注册（IEndpoint 由 UseLedonWeb 统一映射）
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, AdminOpsEndpoints>());

        return services;
    }
}

/// <summary>运维端点选项。</summary>
public sealed class LedonAdminOptions
{
    /// <summary>路由前缀，默认 <c>/admin/ops</c>。</summary>
    public string RoutePrefix { get; set; } = "/admin/ops";
}

/// <summary>运维端点（<see cref="IEndpoint"/> 实现会被自动发现并映射）。</summary>
public sealed class AdminOpsEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/admin/ops");

        // 一页总览：发件箱/队列指标/工作流/定时任务
        group.MapGet("/summary", async (OpsSnapshotService ops, CancellationToken ct) =>
        {
            var snapshot = await ops.CaptureAsync(ct);
            return Results.Ok(snapshot);
        });

        // 死信清单：需要人工介入的搁置消息（未接入发件箱时返回空清单）
        group.MapGet("/outbox/dead-letters", (HttpContext context) =>
        {
            var store = context.RequestServices.GetService<IOutboxStore>();

            return Results.Ok(new { enabled = store is not null, items = store is null ? [] : store.GetDeadLettersAsync(100, CancellationToken.None).GetAwaiter().GetResult() });
        });

        // 重放单条死信（唯一写操作：把消息放回投递队列）
        group.MapPost("/outbox/dead-letters/{id:guid}/replay", (Guid id, HttpContext context) =>
        {
            var store = context.RequestServices.GetService<IOutboxStore>();
            if (store is null)
                return Results.NotFound();

            return store.ReviveAsync(id, CancellationToken.None).GetAwaiter().GetResult()
                ? Results.Ok(new { replayed = id })
                : Results.NotFound();
        });

        // 定时任务与最近执行历史（未接入调度时返回空列表，不报错）
        group.MapGet("/scheduler", (HttpContext context) =>
        {
            var invoker = context.RequestServices.GetService<ISchedulerInvoker>();
            var history = context.RequestServices.GetService<IScheduledTaskExecutionHistory>();

            return Results.Ok(new
            {
                tasks = invoker?.TaskNames ?? [],
                recent = history?.Snapshot().Take(20) ?? [],
            });
        });
    }
}
