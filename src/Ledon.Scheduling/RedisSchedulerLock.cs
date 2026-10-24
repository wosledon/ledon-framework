using StackExchange.Redis;

namespace Ledon.Scheduling;

/// <summary>
/// 基于 Redis 的调度领导权锁（SET NX PX 语义，StackExchange.Redis 的
/// LockTake / LockExtend / LockRelease）。仅 <see cref="SchedulerLeaderElection.Redis"/> 时装配。
/// </summary>
internal sealed class RedisSchedulerLock : ISchedulerLock
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly LedonSchedulingOptions _options;

    // 持锁者标识：实例名 + 随机后缀（同机多进程也能区分）
    private readonly RedisValue _token = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private bool _acquired;

    public RedisSchedulerLock(LedonSchedulingOptions options)
    {
        _options = options;
        _multiplexer = ConnectionMultiplexer.Connect(options.RedisConnectionString!);
    }

    public Task<bool> AcquireAsync(CancellationToken cancellationToken = default)
    {
        _acquired = _multiplexer.GetDatabase().LockTake(_options.LeaderLockKey, _token, _options.LeaderLockExpiry);
        return Task.FromResult(_acquired);
    }

    public Task<bool> RenewAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_multiplexer.GetDatabase().LockExtend(_options.LeaderLockKey, _token, _options.LeaderLockExpiry));

    public Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (!_acquired)
            return Task.CompletedTask;

        _acquired = false;
        _multiplexer.GetDatabase().LockRelease(_options.LeaderLockKey, _token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        await _multiplexer.DisposeAsync();
    }
}
