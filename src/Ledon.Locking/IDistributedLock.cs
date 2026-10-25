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

    /// <summary>
    /// 续期：把过期时间往后推（看门狗场景）。
    /// <para>
    /// 锁是**固定 TTL** 的：临界区执行超过 TTL 时锁已过期，别的进程能拿到同一把锁——
    /// 两个进程同时认为持有（脑裂）。长临界区要么调大 expiry，要么周期性调本方法续期。
    /// </para>
    /// 返回 <c>false</c> 表示锁已丢失（到期/被释放），此时必须停止工作。
    /// </summary>
    ValueTask<bool> RenewAsync(TimeSpan? expiry = null, CancellationToken cancellationToken = default);
}
