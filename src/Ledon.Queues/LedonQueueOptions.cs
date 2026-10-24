namespace Ledon.Queues;

/// <summary>后台队列默认参数。</summary>
public sealed class LedonQueueOptions
{
    /// <summary>并发处理任务的 worker 数，默认 1（保持处理顺序）。</summary>
    public int WorkerCount { get; set; } = 1;

    /// <summary>有界队列容量（满了写入方等待），默认 1000。</summary>
    public int Capacity { get; set; } = 1000;
}
