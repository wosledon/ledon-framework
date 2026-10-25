using System.Collections.Concurrent;

namespace Ledon.Locking;

/// <summary>
/// 进程内锁：按资源标识分桶的信号量。单实例部署零依赖；
/// 多实例部署换 <see cref="LedonLockProvider.Redis"/>（同一进程内仍走本地信号量，行为一致）。
/// </summary>
public sealed class InMemoryDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly LedonLockingOptions _options;

    public InMemoryDistributedLock(LedonLockingOptions options) => _options = options;

    public ValueTask<ILockHandle?> TryAcquireAsync(
        string resource, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var gate = _gates.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(TimeSpan.Zero))
            return ValueTask.FromResult<ILockHandle?>(null);

        var handle = new InMemoryLockHandle(gate, $"{_options.KeyPrefix}:{resource}");
        return ValueTask.FromResult<ILockHandle?>(handle);
    }

    public async ValueTask<ILockHandle> AcquireAsync(
        string resource, TimeSpan? wait = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var gate = _gates.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1));
        var limit = wait ?? _options.DefaultWait;

        if (!await gate.WaitAsync(limit, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException($"等待锁 {resource} 超时（{limit}）");

        return new InMemoryLockHandle(gate, $"{_options.KeyPrefix}:{resource}");
    }

    private sealed class InMemoryLockHandle(SemaphoreSlim gate, string resource) : ILockHandle
    {
        private int _released;

        public string Resource { get; } = resource;

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            ReleaseAsync().GetAwaiter().GetResult();
            return ValueTask.CompletedTask;
        }
    }
}
