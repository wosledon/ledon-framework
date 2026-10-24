using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledon.Data;

/// <summary>
/// 数据库连通性健康检查：对全部已注册的 <see cref="ILedonDbContext"/> 执行
/// <c>CanConnectAsync</c>。由 <c>AddLedonData</c> 自动登记（名 "database"）——
/// 配合 <c>AddLedonHealthChecks</c> + <c>MapLedonReadiness</c> 即可让就绪探针反映数据库状态。
/// </summary>
internal sealed class LedonDbContextHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;

    public LedonDbContextHealthCheck(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // DbContext 是 Scoped：从根容器新建作用域解析
        await using var scope = _services.CreateAsyncScope();

        var contexts = scope.ServiceProvider.GetServices<ILedonDbContext>().ToList();
        if (contexts.Count == 0)
            return HealthCheckResult.Healthy("未注册 ILedonDbContext");

        var failures = new List<string>();
        foreach (var db in contexts)
        {
            try
            {
                // ILedonDbContext 是标记接口；登记的实例都是 DbContext（AddLedonData 只登记 DbContext 类型）
                if (!await ((DbContext)db).Database.CanConnectAsync(cancellationToken))
                    failures.Add($"{db.GetType().Name}: 无法连接");
            }
            catch (Exception ex)
            {
                failures.Add($"{db.GetType().Name}: {ex.Message}");
            }
        }

        return failures.Count == 0
            ? HealthCheckResult.Healthy($"{contexts.Count} 个 DbContext 均可连接")
            : HealthCheckResult.Unhealthy(string.Join("; ", failures));
    }
}
