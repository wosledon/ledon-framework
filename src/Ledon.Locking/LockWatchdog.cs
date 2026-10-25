using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Locking;

/// <summary>锁看门狗选项。</summary>
public sealed class LockWatchdogOptions
{
    /// <summary>续期间隔占锁 TTL 的比例，默认 1/3（锁 TTL 30 秒 → 每 10 秒续一次）。</summary>
    public double RenewRatio { get; set; } = 1.0 / 3.0;

    /// <summary>续期间隔下限，默认 1 秒（锁 TTL 很短时也不至于疯狂打 Redis）。</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// 锁看门狗：持有锁期间周期性续期，锁丢失时立刻通知。
/// <para>
/// 锁是固定 TTL 的：临界区执行超过 TTL 时锁已过期，别的进程能拿到同一把锁，
/// 两个进程同时认为持有（脑裂）。长临界区必须续期——
/// 本类型把"多久续一次"和"丢了怎么办"变成两行代码。
/// </para>
/// <code>
/// await using var lease = await locker.AcquireAsync("job:nightly", expiry: TimeSpan.FromSeconds(30));
/// await using var dog = LockWatchdog.Attach(lease, logger);   // 每 10 秒续一次
/// dog.LockLost += () =&gt; logger.LogWarning("锁丢了，停止工作");
/// await LongRunningWorkAsync(dog.Lifetime);
/// </code>
/// </summary>
public sealed class LockWatchdog : IAsyncDisposable
{
    private readonly ILockHandle _handle;
    private readonly TimeSpan _expiry;
    private readonly LockWatchdogOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;

    /// <summary>创建并启动看门狗（<paramref name="expiry"/> 须与加锁时一致）。</summary>
    public static LockWatchdog Attach(
        ILockHandle handle,
        ILogger logger,
        TimeSpan? expiry = null,
        LockWatchdogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(logger);

        return new LockWatchdog(handle, logger, expiry, options);
    }

    private LockWatchdog(
        ILockHandle handle,
        ILogger logger,
        TimeSpan? expiry,
        LockWatchdogOptions? options)
    {
        _handle = handle;
        _logger = logger;
        _options = options ?? new LockWatchdogOptions();
        _expiry = expiry ?? TimeSpan.FromSeconds(30);

        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    /// <summary>
    /// 锁仍然有效的生命周期令牌：续期失败/锁丢失时取消。
    /// 长任务应该把这个令牌传给内部操作——丢了锁就该停，而不是继续写。
    /// </summary>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>锁丢失（到期/被释放）时触发一次。此时必须停止临界区工作。</summary>
    public event Func<Task>? LockLost;

    /// <summary>累计续期次数（观测用）。</summary>
    public int RenewCount { get; private set; }

    private async Task LoopAsync()
    {
        var interval = ComputeInterval();

        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(interval, _lifetime.Token).ConfigureAwait(false);

                if (!await _handle.RenewAsync(_expiry, _lifetime.Token).ConfigureAwait(false))
                {
                    _logger.LogWarning("锁 {Resource} 已丢失（到期或被释放）：临界区必须停止", _handle.Resource);

                    await OnLostAsync().ConfigureAwait(false);

                    return;
                }

                RenewCount++;
                _logger.LogDebug("锁 {Resource} 已续期（第 {Count} 次）", _handle.Resource, RenewCount);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            // 续期调用本身失败（Redis 抖动）：保守起见视为锁已丢
            _logger.LogWarning(ex, "锁 {Resource} 续期失败，按丢失处理", _handle.Resource);

            await OnLostAsync().ConfigureAwait(false);
        }
    }

    private TimeSpan ComputeInterval()
    {
        var ticks = _expiry.Ticks * Math.Clamp(_options.RenewRatio, 0.05, 0.9);

        return TimeSpan.FromTicks(Math.Max((long)ticks, _options.MinInterval.Ticks));
    }

    private async Task OnLostAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (LockLost is { } handler)
        {
            try
            {
                await handler().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "锁丢失回调执行失败");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 已取消
        }

        _lifetime.Dispose();
    }
}

/// <summary>看门狗扩展（少写一个类型名）。</summary>
public static class LockWatchdogExtensions
{
    /// <summary>给锁句柄挂上看门狗（锁丢失时取消 <see cref="LockWatchdog.Lifetime"/>）。</summary>
    public static LockWatchdog WithWatchdog(
        this ILockHandle handle,
        ILogger logger,
        TimeSpan? expiry = null,
        LockWatchdogOptions? options = null) =>
        LockWatchdog.Attach(handle, logger, expiry, options);
}
