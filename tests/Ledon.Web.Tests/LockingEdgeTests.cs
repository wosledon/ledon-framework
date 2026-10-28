using Ledon.Locking;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Ledon.Web.Tests;

/// <summary>分布式锁边界：看门狗续期/丢失、Redis 锁令牌校验、等待超时。</summary>
public class LockingEdgeTests
{
    [Fact]
    public async Task Watchdog_renews_until_disposed()
    {
        var handle = new FakeHandle(renew: true);
        await using var watchdog = LockWatchdog.Attach(
            handle,
            NullLogger.Instance,
            expiry: TimeSpan.FromMilliseconds(60),
            new LockWatchdogOptions { MinInterval = TimeSpan.FromMilliseconds(10), RenewRatio = 0.5 });

        await Task.Delay(120);

        Assert.True(handle.Renews > 0);
        Assert.False(watchdog.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task Watchdog_cancels_lifetime_when_lock_is_lost()
    {
        var handle = new FakeHandle(renew: false);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watchdog = LockWatchdog.Attach(
            handle,
            NullLogger.Instance,
            expiry: TimeSpan.FromMilliseconds(60),
            new LockWatchdogOptions { MinInterval = TimeSpan.FromMilliseconds(10), RenewRatio = 0.5 });
        watchdog.LockLost += () =>
        {
            lost.TrySetResult();
            return Task.CompletedTask;
        };

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(watchdog.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public void Watchdog_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => LockWatchdog.Attach(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => LockWatchdog.Attach(new FakeHandle(true), null!));
    }

    [Fact]
    public async Task Redis_lock_acquires_releases_and_checks_token()
    {
        var (locker, database) = BuildRedisLock(acquired: true);

        var handle = await locker.TryAcquireAsync("orders:1");
        Assert.NotNull(handle);

        await handle!.ReleaseAsync();
        await handle.ReleaseAsync();   // 幂等

        database.Verify(d => d.ScriptEvaluateAsync(
            It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()),
            Times.Once);
        Assert.False(await handle.RenewAsync());
    }

    [Fact]
    public async Task Redis_lock_returns_null_when_not_acquired()
    {
        var (locker, _) = BuildRedisLock(acquired: false);

        Assert.Null(await locker.TryAcquireAsync("orders:1"));
    }

    [Fact]
    public async Task Redis_lock_waits_then_times_out()
    {
        var (locker, _) = BuildRedisLock(acquired: false);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await locker.AcquireAsync("orders:1", wait: TimeSpan.FromMilliseconds(40)));
    }

    private static (RedisDistributedLock Locker, Mock<IDatabase> Database) BuildRedisLock(bool acquired)
    {
        var database = new Mock<IDatabase>();
        database
            .Setup(d => d.LockTake(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CommandFlags>()))
            .Returns(acquired);
        database
            .Setup(d => d.StringSet(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .Returns(acquired);
        database
            .Setup(d => d.StringSet(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists,
                CommandFlags.None))
            .Returns(acquired);
        database
            .Setup(d => d.StringSet(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                When.NotExists,
                CommandFlags.None))
            .Returns(acquired);
        database
            .Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create(new RedisValue("1")));

        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        multiplexer.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var locker = new RedisDistributedLock(
            new LedonLockingOptions { RedisConnectionString = "localhost:6379" },
            multiplexer.Object);

        return (locker, database);
    }

    private sealed class FakeHandle(bool renew) : ILockHandle
    {
        public int Renews { get; private set; }

        public string Resource => "test:lock";

        public Task ReleaseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<bool> RenewAsync(TimeSpan? expiry = null, CancellationToken cancellationToken = default)
        {
            Renews++;
            return ValueTask.FromResult(renew);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
