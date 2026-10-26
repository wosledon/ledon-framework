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

        options.RoutePrefix = NormalizeRoutePrefix(options.RoutePrefix);

        services.TryAddSingleton(options);
        services.TryAddSingleton(_ => new QueueMetricsReader().Start());
        services.TryAddSingleton<OpsSnapshotService>();

        // 框架包自身不进约定扫描，端点显式注册（IEndpoint 由 UseLedonWeb 统一映射）
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpoint, AdminOpsEndpoints>());

        return services;
    }

    /// <summary>规范化路由前缀：必须以 <c>/</c> 开头、不能以 <c>/</c> 结尾（根路径除外）。</summary>
    private static string NormalizeRoutePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new InvalidOperationException("LedonAdminOptions.RoutePrefix 不能为空");

        var normalized = "/" + prefix.Trim().Trim('/');

        return normalized.Length == 1 ? "/" : normalized;
    }
}

/// <summary>运维端点选项。</summary>
public sealed class LedonAdminOptions
{
    /// <summary>路由前缀，默认 <c>/admin/ops</c>（首尾斜杠会自动规范化）。</summary>
    public string RoutePrefix { get; set; } = "/admin/ops";

    /// <summary>
    /// 是否要求运维端点通过授权，默认 <c>false</c>（保持零配置可用）。
    /// <para>
    /// 生产环境建议开启，并配合 <c>AddLedonSecurity()</c> 或自定义认证方案：
    /// 快照与死信清单会暴露内部运行信息。开启但未注册任何认证方案时，启动会直接失败，
    /// 而不是静默放行。
    /// </para>
    /// </summary>
    public bool RequireAuthorization { get; set; }
}

/// <summary>运维端点（<see cref="IEndpoint"/> 实现会被自动发现并映射）。</summary>
public sealed class AdminOpsEndpoints(LedonAdminOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        if (options.RequireAuthorization && !HasAuthenticationScheme(builder.ServiceProvider))
        {
            throw new InvalidOperationException(
                "LedonAdminOptions.RequireAuthorization = true，但未注册任何认证方案。" +
                "请先调用 AddLedonSecurity()/AddAuthentication()，否则运维端点会处于无保护状态。");
        }

        var group = builder.MapGroup(options.RoutePrefix);

        if (options.RequireAuthorization)
            group.RequireAuthorization();

        // 一页总览：发件箱/队列指标/工作流/定时任务
        group.MapGet("/summary", async (OpsSnapshotService ops, CancellationToken ct) =>
        {
            var snapshot = await ops.CaptureAsync(ct);
            return Results.Ok(snapshot);
        });

        // 死信清单：需要人工介入的搁置消息（未接入发件箱时返回空清单）
        group.MapGet("/outbox/dead-letters", async (HttpContext context, CancellationToken ct) =>
        {
            var store = context.RequestServices.GetService<IOutboxStore>();
            IReadOnlyList<OutboxMessage> items = store is null
                ? []
                : await store.GetDeadLettersAsync(100, ct).ConfigureAwait(false);

            return Results.Ok(new { enabled = store is not null, items });
        });

        // 重放单条死信（唯一写操作：把消息放回投递队列）
        group.MapPost("/outbox/dead-letters/{id:guid}/replay", async (Guid id, HttpContext context, CancellationToken ct) =>
        {
            var store = context.RequestServices.GetService<IOutboxStore>();
            if (store is null)
                return Results.NotFound();

            var replayed = await store.ReviveAsync(id, ct).ConfigureAwait(false);

            return replayed
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

    private static bool HasAuthenticationScheme(IServiceProvider services)
    {
        var authentication = services
            .GetService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Authentication.AuthenticationOptions>>();

        if (authentication is null)
            return false;

        return authentication.Value.Schemes.Any();
    }
}
