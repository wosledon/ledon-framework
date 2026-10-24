using System.Threading.Channels;

namespace Ledon.Queues;

/// <summary>后台任务的工作项类型：接收一个 DI 作用域提供器与取消令牌。</summary>
internal sealed record WorkItem(Func<IServiceProvider, CancellationToken, Task> Execute);

/// <summary>
/// 基于 <see cref="Channel{T}"/> 的有界后台队列。写入方在队列满时等待（背压）。
/// </summary>
internal sealed class ChannelBackgroundQueue : IBackgroundQueue
{
    private readonly Channel<WorkItem> _channel;

    public ChannelBackgroundQueue(Channel<WorkItem> channel) => _channel = channel;

    public async ValueTask EnqueueAsync(
        Func<IServiceProvider, CancellationToken, Task> workItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        await _channel.Writer.WriteAsync(new WorkItem(workItem), cancellationToken).ConfigureAwait(false);
    }
}
