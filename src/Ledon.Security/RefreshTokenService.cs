using System.Security.Cryptography;
using System.Text;

namespace Ledon.Security;

/// <summary>
/// 刷新令牌服务：签发 / 校验 / 轮换。刷新令牌为 256-bit 随机不透明串，
/// 服务端只存其 SHA-256 哈希（库被拖库也换不出可用令牌）。
/// 典型流程：登录时 <see cref="IssueAsync"/> 与 IJwtTokenGenerator.Generate 一起返回客户端；
/// 访问令牌过期时客户端用刷新令牌 <see cref="RotateAsync"/> 换新的（旧的随即作废）。
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>为指定用户签发刷新令牌。</summary>
    Task<string> IssueAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验刷新令牌，返回持有者 userId；不存在 / 已过期 / 已吊销返回 null。
    /// 检测到已吊销令牌被再次使用（重放）时，吊销该用户全部令牌并返回 null。
    /// </summary>
    Task<string?> ValidateAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>轮换：校验通过则吊销旧令牌并签发新令牌；校验失败返回 null。</summary>
    Task<string?> RotateAsync(string refreshToken, CancellationToken cancellationToken = default);
}

internal sealed class RefreshTokenService(
    IRefreshTokenStore store,
    LedonSecurityOptions options) : IRefreshTokenService
{
    public async Task<string> IssueAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await store.StoreAsync(
            userId,
            Hash(token),
            DateTimeOffset.UtcNow.Add(options.RefreshTokenExpiration),
            cancellationToken);
        return token;
    }

    public async Task<string?> ValidateAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var entry = await store.FindAsync(Hash(refreshToken), cancellationToken).ConfigureAwait(false);

        // 重放检测：已吊销令牌被再次使用 → 该用户全部令牌作废（防止窃取的旧令牌继续换新）
        if (entry is { Revoked: true })
        {
            await store.RevokeAllForUserAsync(entry.UserId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (entry is null || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;

        return entry.UserId;
    }

    public async Task<string?> RotateAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var userId = await ValidateAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return null;

        await store.RevokeAsync(Hash(refreshToken), cancellationToken).ConfigureAwait(false);
        return await IssueAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
