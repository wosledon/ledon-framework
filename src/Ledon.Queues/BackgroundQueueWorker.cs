using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Queues;

/// <summary>
/// 后台队列 worker：从队列取任务，在独立 DI 作用域中执行。
/// 单个任务失败只记录日志并继续，不会拖垮 worker。
/// </summary>
internal sealed class BackgroundQueueWorker : BackgroundService
{
    private readonly Channel<WorkItem> _channel;
    private readonly IServiceProvider _services;
    private readonly ILogger<BackgroundQueueWorker> _logger;

    public BackgroundQueueWorker(
        Channel<WorkItem> channel,
        IServiceProvider services,
        ILogger<BackgroundQueueWorker> logger)
    {
        _channel = channel;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            await using var scope = _services.CreateAsyncScope();

            try
            {
                await item.Execute(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 停机中的取消：正常路径
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台任务执行失败（已跳过，不影响后续任务）");
            }
        }
    }
}
