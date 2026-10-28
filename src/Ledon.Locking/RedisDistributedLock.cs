using StackExchange.Redis;

namespace Ledon.Locking;

/// <summary>
/// 基于 Redis 的锁（SET NX PX 语义 + Lua 校验令牌释放）。
/// 多实例部署下互斥；持锁进程崩溃由 <see cref="LedonLockingOptions.DefaultExpiry"/> 兜底回收。
/// </summary>
public sealed class RedisDistributedLock : IDistributedLock, IAsyncDisposable
{
    // 只释放自己持有的锁：比较令牌再删，避免"锁已到期被他人拿走"时误删
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    // 续期：令牌相符才延命，返回值即"锁还是不是我的"
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            return -1
        end
        """;

    private readonly LedonLockingOptions _options;
    private readonly IConnectionMultiplexer _multiplexer;

    // 持锁者标识：机器名 + 随机后缀（同机多进程可区分）
    private readonly RedisValue _token = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public RedisDistributedLock(LedonLockingOptions options)
        : this(options, ConnectionMultiplexer.Connect(options.RedisConnectionString!))
    {
    }

    /// <summary>注入多路复用器（测试/自定义连接生命周期时使用）。</summary>
    public RedisDistributedLock(LedonLockingOptions options, IConnectionMultiplexer multiplexer)
    {
        _options = options;
        _multiplexer = multiplexer;
    }

    public ValueTask<ILockHandle?> TryAcquireAsync(
        string resource, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var acquired = _multiplexer.GetDatabase()
            .LockTake(Key(resource), _token, expiry ?? _options.DefaultExpiry);

        return ValueTask.FromResult<ILockHandle?>(acquired
            ? new RedisLockHandle(this, Key(resource), expiry ?? _options.DefaultExpiry)
            : null);
    }

    public async ValueTask<ILockHandle> AcquireAsync(
        string resource, TimeSpan? wait = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var limit = wait ?? _options.DefaultWait;
        var deadline = DateTimeOffset.UtcNow + limit;

        while (true)
        {
            var handle = await TryAcquireAsync(resource, expiry, cancellationToken).ConfigureAwait(false);
            if (handle is not null)
                return handle;

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"等待锁 {resource} 超时（{limit}）");

            await Task.Delay(RetryInterval(limit), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await _multiplexer.DisposeAsync().ConfigureAwait(false);

    private string Key(string resource) => $"{_options.KeyPrefix}:{resource}";

    // 等待越短，重试越密（上限 100ms）：短等待下尽快抢到，长等待下不打爆 Redis
    private static TimeSpan RetryInterval(TimeSpan wait) =>
        TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds / 20, 10, 100));

    private sealed class RedisLockHandle(RedisDistributedLock owner, string key, TimeSpan expiry) : ILockHandle
    {
        private int _released;

        public string Resource { get; } = key;

        public async Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            await owner._multiplexer.GetDatabase()
                .ScriptEvaluateAsync(ReleaseScript, [Resource], [owner._token])
                .ConfigureAwait(false);
        }

        public async ValueTask<bool> RenewAsync(TimeSpan? extension = null, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _released) != 0)
                return false;

            var window = extension ?? expiry;

            // Lua 保证"还是我的"与"续期"是原子操作：并发释放/到期不会把别人的锁续命
            var result = await owner._multiplexer.GetDatabase()
                .ScriptEvaluateAsync(RenewScript, [Resource], [owner._token, (long)window.TotalMilliseconds])
                .ConfigureAwait(false);

            return Convert.ToString(result) != "-1";
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }
}
