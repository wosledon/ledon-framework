using System.Diagnostics;
using System.Threading.Channels;
using Ledon.Module.Diagnostics;
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
    private readonly LedonQueueOptions _options;
    private readonly ILogger<BackgroundQueueWorker> _logger;

    public BackgroundQueueWorker(
        Channel<WorkItem> channel,
        IServiceProvider services,
        LedonQueueOptions options,
        ILogger<BackgroundQueueWorker> logger)
    {
        _channel = channel;
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkItem? pending = null;

        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // ReadAllAsync 对已缓冲的条目不观察取消：停机后主动收手。
                // 注意条目此时已出列——交给 drain 处理，不能直接丢
                if (stoppingToken.IsCancellationRequested)
                {
                    pending = item;
                    break;
                }

                await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机：正常路径——转到 drain，把已入队未处理的任务处理完
        }

        await DrainAsync(pending).ConfigureAwait(false);
    }

    /// <summary>停机 drain：有界地处理完队列里已入队的任务，尽量避免优雅退出丢任务。</summary>
    private async Task DrainAsync(WorkItem? pending)
    {
        if (_options.DrainTimeout <= TimeSpan.Zero)
        {
            DropPending(pending, "未配置 drain 时间（LedonQueueOptions.DrainTimeout = 0）");
            return;
        }

        using var timeout = new CancellationTokenSource(_options.DrainTimeout);
        var drained = 0;

        // 先处理停机时已取出未执行的条目，再排空队列里剩下的
        foreach (var item in Prepend(pending, _channel.Reader))
        {
            try
            {
                await ProcessAsync(item, timeout.Token).ConfigureAwait(false);
                drained++;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                DropPending(null, $"drain 超过 {_options.DrainTimeout.TotalSeconds:0.#}s");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "drain 期间后台任务失败（已丢弃该任务）");
            }
        }

        if (drained > 0)
            _logger.LogInformation("停机 drain：处理完 {Count} 个已入队的后台任务", drained);
    }

    /// <summary>已取出未执行的条目 + 队列中剩余条目（按入队顺序）。</summary>
    private static IEnumerable<WorkItem> Prepend(WorkItem? pending, ChannelReader<WorkItem> reader)
    {
        if (pending is not null)
            yield return pending;

        while (reader.TryRead(out var item))
            yield return item;
    }

    private void DropPending(WorkItem? pending, string reason)
    {
        var dropped = pending is not null ? 1 : 0;
        while (_channel.Reader.TryRead(out _))
            dropped++;

        if (dropped > 0)
            _logger.LogWarning("停机时丢弃 {Count} 个未处理的后台任务（{Reason}）", dropped, reason);
    }

    private async Task ProcessAsync(WorkItem item, CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();

        // 每个后台任务一个 span：队列里的"黑盒执行"变得可观测
        using var activity = LedonActivity.Queues.StartActivity("Queue.Process");

        try
        {
            await item.Execute(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            QueueMetrics.QueueProcessed.Add(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机/drain 超时中的取消：正常路径
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "后台任务执行失败（已跳过，不影响后续任务）");
            QueueMetrics.QueueFailed.Add(1);
        }
    }
}
