namespace Ledon.Queues;

/// <summary>
/// 后台任务队列：把工作丢进队列由 worker 异步处理，不阻塞请求线程。
/// 每个任务在执行时都会拿到一个独立的 DI 作用域（<see cref="IServiceProvider"/> 即作用域提供器），
/// 可安全解析 Scoped 服务。worker 崩溃不会影响其它任务（失败记录日志后继续）。
/// v1 为进程内 Channel 实现；接外部 Broker（RabbitMQ/Kafka 等）时替换实现即可。
/// </summary>
public interface IBackgroundQueue
{
    /// <summary>入队一个后台任务。</summary>
    ValueTask EnqueueAsync(
        Func<IServiceProvider, CancellationToken, Task> workItem,
        CancellationToken cancellationToken = default);
}
