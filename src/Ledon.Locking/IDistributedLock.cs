namespace Ledon.Locking;

/// <summary>
/// 跨进程互斥锁（防重、资源独占、定时任务防重入）。进程内与 Redis 两种实现，
/// 业务代码只依赖抽象：
/// <code>
/// public class OrderService(IDistributedLock locker)
/// {
///     public async Task PayAsync(string orderId, CancellationToken ct)
///     {
///         await using var handle = await locker.AcquireAsync($"order:{orderId}", ct: ct);
///         // 同一订单的并发支付被串行化（多实例部署同样有效）
///     }
/// }
/// </code>
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// 尝试加锁：拿到返回 <see cref="ILockHandle"/>，已被占用返回 <c>null</c>（不等待）。
    /// </summary>
    ValueTask<ILockHandle?> TryAcquireAsync(
        string resource,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 加锁并等待：<paramref name="wait"/> 内反复重试，超时抛 <see cref="TimeoutException"/>。
    /// </summary>
    ValueTask<ILockHandle> AcquireAsync(
        string resource,
        TimeSpan? wait = null,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default);
}

/// <summary>锁句柄：释放即解锁（<see cref="IAsyncDisposable"/> 也会释放）。</summary>
public interface ILockHandle : IAsyncDisposable
{
    /// <summary>锁定的资源标识（含键前缀）。</summary>
    string Resource { get; }

    /// <summary>显式释放。幂等；进程崩溃时由过期时间兜底回收。</summary>
    Task ReleaseAsync(CancellationToken cancellationToken = default);
}
