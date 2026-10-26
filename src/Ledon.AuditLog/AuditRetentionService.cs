using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.AuditLog;

/// <summary>
/// 审计保留策略：按 <see cref="LedonAuditOptions.RetentionDays"/> 定期清理过期流水。
/// 启动后立即执行一次（清掉停机期间堆积的旧数据），之后按
/// <see cref="LedonAuditOptions.RetentionSweepInterval"/> 巡检。
/// </summary>
internal sealed class AuditRetentionService(
    IServiceScopeFactory scopeFactory,
    LedonAuditOptions options,
    ILogger<AuditRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 0 = 保留策略关闭
        if (options.RetentionDays <= 0)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(options.RetentionSweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAuditStore>();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.RetentionDays);

            var purged = await store.PurgeOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);

            if (purged > 0)
                logger.LogInformation("审计保留策略清理了 {Count} 条早于 {Cutoff:O} 的记录", purged, cutoff);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机中取消
        }
        catch (Exception ex)
        {
            // 清理失败不能拖垮宿主：记录后下个周期重试
            logger.LogWarning(ex, "审计保留策略清理失败（下次继续）");
        }
    }
}
