using Ledon.Locking;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>分布式锁（进程内实现）：互斥、释放、超时、并发。</summary>
public class DistributedLockTests
{
    private static IDistributedLock Build(Action<LedonLockingOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLedonLocking(configure ?? (_ => { }));
        return services.BuildServiceProvider().GetRequiredService<IDistributedLock>();
    }

    [Fact]
    public async Task Second_acquire_fails_while_held()
    {
        var locker = Build();

        await using var first = await locker.AcquireAsync("res:a", wait: TimeSpan.Zero);
        var second = await locker.TryAcquireAsync("res:a");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task Release_allows_reacquire()
    {
        var locker = Build();

        var first = await locker.AcquireAsync("res:a", wait: TimeSpan.Zero);
        await first.ReleaseAsync();

        var second = await locker.TryAcquireAsync("res:a");
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_releases_the_lock()
    {
        var locker = Build();

        await using (await locker.AcquireAsync("res:a", wait: TimeSpan.Zero))
        {
            Assert.Null(await locker.TryAcquireAsync("res:a"));
        }

        Assert.NotNull(await locker.TryAcquireAsync("res:a"));
    }

    [Fact]
    public async Task Different_resources_are_independent()
    {
        var locker = Build();

        await using var a = await locker.AcquireAsync("res:a", wait: TimeSpan.Zero);
        await using var b = await locker.TryAcquireAsync("res:b");

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Acquire_times_out_when_wait_exceeds()
    {
        var locker = Build();

        await using var held = await locker.AcquireAsync("res:a", wait: TimeSpan.Zero);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            locker.AcquireAsync("res:a", wait: TimeSpan.FromMilliseconds(50)).AsTask());
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_acquires_succeeds()
    {
        var locker = Build();
        const int contenders = 20;

        var handles = await Task.WhenAll(Enumerable.Range(0, contenders)
            .Select(_ => locker.TryAcquireAsync("res:hot").AsTask()));

        Assert.Equal(1, handles.Count(handle => handle is not null));

        foreach (var handle in handles.OfType<ILockHandle>())
            await handle.DisposeAsync();
    }

    [Fact]
    public async Task Handle_exposes_prefixed_resource_key()
    {
        var locker = Build(o => o.KeyPrefix = "app:lock");

        var handle = await locker.TryAcquireAsync("order:1");

        Assert.NotNull(handle);
        Assert.Equal("app:lock:order:1", handle!.Resource);
        await handle.DisposeAsync();
    }
}
