using System.Collections.Concurrent;

namespace Ledon.Security;

/// <summary>
/// 进程内刷新令牌存储：线程安全字典，按过期时间惰性清理（写入/查询时顺带清理，
/// 至少每分钟一次）——否则被吊销/过期的令牌会永久占用内存。
/// 多实例部署需替换为共享存储（Redis 等）——见 <see cref="IRefreshTokenStore"/>。
/// </summary>
internal sealed class InMemoryRefreshTokenStore(TimeProvider timeProvider) : IRefreshTokenStore
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, RefreshTokenEntry> _tokens = new();
    private long _nextSweepUtcTicks;

    public Task StoreAsync(string userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        SweepExpiredIfDue();

        _tokens[tokenHash] = new RefreshTokenEntry(userId, expiresAt, Revoked: false);
        return Task.CompletedTask;
    }

    public Task<RefreshTokenEntry?> FindAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        SweepExpiredIfDue();

        if (!_tokens.TryGetValue(tokenHash, out var entry))
            return Task.FromResult<RefreshTokenEntry?>(null);

        // 单条过期立即剔除，不等下一轮全量清理
        if (entry.ExpiresAt <= timeProvider.GetUtcNow())
        {
            _tokens.TryRemove(tokenHash, out _);
            return Task.FromResult<RefreshTokenEntry?>(null);
        }

        return Task.FromResult<RefreshTokenEntry?>(entry);
    }

    public Task RevokeAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        if (_tokens.TryGetValue(tokenHash, out var entry))
            _tokens[tokenHash] = entry with { Revoked = true };

        return Task.CompletedTask;
    }

    public Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        foreach (var (hash, entry) in _tokens)
        {
            if (entry.UserId == userId)
                _tokens[hash] = entry with { Revoked = true };
        }

        return Task.CompletedTask;
    }

    private void SweepExpiredIfDue()
    {
        var now = timeProvider.GetUtcNow();
        var nextSweep = Interlocked.Read(ref _nextSweepUtcTicks);

        if (now.UtcDateTime.Ticks < nextSweep)
            return;

        // 只有抢到本轮清理权的线程执行全量扫描，避免每次请求都 O(n)
        if (Interlocked.CompareExchange(
                ref _nextSweepUtcTicks,
                now.Add(SweepInterval).UtcDateTime.Ticks,
                nextSweep) != nextSweep)
        {
            return;
        }

        foreach (var (hash, entry) in _tokens)
        {
            if (entry.ExpiresAt <= now)
                _tokens.TryRemove(hash, out _);
        }
    }
}
