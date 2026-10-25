namespace Ledon.Locking;

/// <summary>锁提供程序。</summary>
public enum LedonLockProvider
{
    /// <summary>进程内锁（单实例部署；默认）。</summary>
    InMemory = 0,

    /// <summary>Redis 锁（多实例部署）。</summary>
    Redis = 1,
}

/// <summary>锁选项。</summary>
public sealed class LedonLockingOptions
{
    /// <summary>提供程序，默认 <see cref="LedonLockProvider.InMemory"/>。</summary>
    public LedonLockProvider Provider { get; set; } = LedonLockProvider.InMemory;

    /// <summary>Redis 连接串（<see cref="LedonLockProvider.Redis"/> 必填）。</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>锁键前缀，默认 <c>ledon:lock</c>。</summary>
    public string KeyPrefix { get; set; } = "ledon:lock";

    /// <summary>默认持锁时长，默认 30 秒。到期自动释放，防持锁进程崩溃死锁。</summary>
    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><see cref="IDistributedLock.AcquireAsync"/> 的默认等待时长，默认 10 秒。</summary>
    public TimeSpan DefaultWait { get; set; } = TimeSpan.FromSeconds(10);
}
