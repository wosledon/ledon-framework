namespace Ledon.Queues;

/// <summary>后台队列默认参数。</summary>
public sealed class LedonQueueOptions
{
    /// <summary>并发处理任务的 worker 数，默认 1（保持处理顺序）。</summary>
    public int WorkerCount { get; set; } = 1;

    /// <summary>有界队列容量（满了写入方等待），默认 1000。</summary>
    public int Capacity { get; set; } = 1000;

    /// <summary>
    /// 停机 drain 时长（默认 5 秒）：宿主停止时把队列中已入队、未处理的任务继续处理完
    /// （有界），避免优雅退出丢任务。设为 <see cref="TimeSpan.Zero"/> 则不 drain，直接丢弃
    /// （丢弃数量记 Warning 日志）。正在执行中的任务由停机令牌取消，语义不变。
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
