namespace Ledon.Security;

/// <summary>
/// 刷新令牌存储（契约）。刷新令牌为随机不透明串，存储与比对一律使用其 SHA-256 哈希——
/// 原始令牌只存在于客户端。默认进程内实现；多实例部署替换为 Redis 等共享存储
/// （实现本接口并注册为 <see cref="IRefreshTokenStore"/>，框架的 TryAdd 注册让位于手动注册）。
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>存入令牌哈希（含过期时间）。</summary>
    Task StoreAsync(string userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>按哈希查找；不存在返回 null。</summary>
    Task<RefreshTokenEntry?> FindAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>吊销单个令牌。</summary>
    Task RevokeAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>吊销某用户的全部令牌（改密码/检测到重放时用）。</summary>
    Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>刷新令牌存储条目。</summary>
/// <param name="UserId">持有者。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <param name="Revoked">是否已吊销（轮换后旧令牌即吊销）。</param>
public sealed record RefreshTokenEntry(string UserId, DateTimeOffset ExpiresAt, bool Revoked);
