using System.Security.Cryptography;
using System.Text;

namespace Ledon.Streaming;

/// <summary>
/// 流令牌服务：推流/拉流地址的防篡改鉴权（HMAC-SHA256 签名 + 过期时间）。
/// <para>
/// 签名字符串 = <c>{action}:{app}/{name}:{expireUnixSeconds}[:{extra}]</c>，
/// 与常见流媒体服务器的 token 鉴权思路一致：地址过期即失效，改一个字符就验不过。
/// </para>
/// </summary>
public interface IStreamTokenService
{
    /// <summary>签发令牌。</summary>
    /// <param name="identity">流标识。</param>
    /// <param name="action">推流还是拉流。</param>
    /// <param name="lifetime">有效期；<c>null</c> 用默认值。</param>
    /// <param name="extra">附加绑定信息（如用户 ID，令牌只对该用户有效）。</param>
    StreamToken CreateToken(
        StreamIdentity identity,
        StreamAction action,
        TimeSpan? lifetime = null,
        string? extra = null);

    /// <summary>校验令牌；成功返回解码出的信息。</summary>
    bool Validate(StreamIdentity identity, StreamAction action, string? token, string? extra = null);
}

/// <summary>已签发的令牌。</summary>
public sealed record StreamToken(string Value, DateTimeOffset ExpiresAtUtc)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>默认实现：HMAC-SHA256。</summary>
public sealed class HmacStreamTokenService : IStreamTokenService
{
    private readonly LedonStreamingOptions _options;
    private readonly byte[] _key;

    public HmacStreamTokenService(LedonStreamingOptions options)
    {
        _options = options;

        if (string.IsNullOrWhiteSpace(options.Secret))
            throw new InvalidOperationException("流鉴权需要配置 Secret（LedonStreamingOptions.Secret）");

        _key = Encoding.UTF8.GetBytes(options.Secret);
    }

    /// <summary>默认有效期（未显式指定时用）。</summary>
    public TimeSpan DefaultLifetime => _options.TokenLifetime;

    public StreamToken CreateToken(
        StreamIdentity identity,
        StreamAction action,
        TimeSpan? lifetime = null,
        string? extra = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime ?? _options.TokenLifetime);
        var value = Sign(identity, action, expiresAt, extra);

        return new StreamToken(value, expiresAt);
    }

    public bool Validate(StreamIdentity identity, StreamAction action, string? token, string? extra = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        // 令牌 = "签名-过期时间戳"：过期时间明文携带，服务端可快速拒绝过期令牌
        var separator = token.LastIndexOf('-');
        if (separator <= 0 || separator == token.Length - 1)
            return false;

        var signature = token[..separator];
        var expiryText = token[(separator + 1)..];

        if (!long.TryParse(expiryText, out var expirySeconds))
            return false;

        if (DateTimeOffset.FromUnixTimeSeconds(expirySeconds) < DateTimeOffset.UtcNow)
            return false;   // 已过期

        var expected = Signature(identity, action, DateTimeOffset.FromUnixTimeSeconds(expirySeconds), extra);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(expected));
    }

    private string Sign(
        StreamIdentity identity,
        StreamAction action,
        DateTimeOffset? expiresAt,
        string? extra)
    {
        var expiry = (expiresAt ?? DateTimeOffset.UtcNow.Add(_options.TokenLifetime)).ToUnixTimeSeconds();

        return $"{Signature(identity, action, DateTimeOffset.FromUnixTimeSeconds(expiry), extra)}-{expiry}";
    }

    private string Signature(
        StreamIdentity identity,
        StreamAction action,
        DateTimeOffset expiresAt,
        string? extra)
    {
        var expiry = expiresAt.ToUnixTimeSeconds().ToString();

        var payload = string.IsNullOrEmpty(extra)
            ? $"{ActionName(action)}:{identity.Path}:{expiry}"
            : $"{ActionName(action)}:{identity.Path}:{expiry}:{extra}";

        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        // base32url：去掉填充并把 +/ 换成 -_ （不含 '.'，便于拼接到 URL）
        return Base64Url(hash);
    }

    private static string ActionName(StreamAction action) =>
        action == StreamAction.Publish ? "pub" : "play";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
