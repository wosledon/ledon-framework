using System.Collections.Concurrent;

namespace Ledon.Security;

/// <summary>
/// 进程内刷新令牌存储：线程安全字典，按过期时间惰性清理。
/// 多实例部署需替换为共享存储（Redis 等）——见 <see cref="IRefreshTokenStore"/>。
/// </summary>
internal sealed class InMemoryRefreshTokenStore : IRefreshTokenStore
{
    private readonly ConcurrentDictionary<string, RefreshTokenEntry> _tokens = new();

    public Task StoreAsync(string userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        _tokens[tokenHash] = new RefreshTokenEntry(userId, expiresAt, Revoked: false);
        return Task.CompletedTask;
    }

    public Task<RefreshTokenEntry?> FindAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.TryGetValue(tokenHash, out var entry) ? entry : null);

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
}
