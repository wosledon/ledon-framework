using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Ledon.Security.ApiKeys;

/// <summary>动态 API Key 选项。</summary>
public sealed class ApiKeyManagementOptions
{
    /// <summary>请求头名，默认 <c>X-Api-Key</c>。</summary>
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>Key 前缀（可读标识），默认 <c>ledon</c>。</summary>
    public string Prefix { get; set; } = "ledon";

    /// <summary>默认有效期；为空表示永不过期。</summary>
    public TimeSpan? DefaultLifetime { get; set; }

    /// <summary>管理端点是否要求授权，默认 <c>false</c>（生产建议开启）。</summary>
    public bool RequireAuthorization { get; set; }
}

/// <summary>API Key 记录（只保存哈希）。</summary>
public sealed record ApiKeyRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    /// <summary>可读前缀（前 8 位），用于识别。</summary>
    public required string Prefix { get; init; }

    /// <summary>SHA-256 十六进制哈希。</summary>
    public required string KeyHash { get; init; }

    public string? TenantId { get; init; }

    public IReadOnlyCollection<string> Scopes { get; init; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public DateTimeOffset? LastUsedAtUtc { get; init; }

    public bool Revoked { get; init; }

    /// <summary>是否有效（未吊销且未过期）。</summary>
    public bool IsActive(DateTimeOffset now) => !Revoked && (ExpiresAtUtc is null || ExpiresAtUtc > now);
}

/// <summary>创建结果：明文 Key 只在创建时返回一次。</summary>
public sealed record ApiKeyCreated(ApiKeyRecord Key, string Plaintext);

/// <summary>API Key 存储（默认内存；可替换为 Redis/数据库实现）。</summary>
public interface IApiKeyStore
{
    Task AddAsync(ApiKeyRecord key, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiKeyRecord>> ListAsync(string? tenantId = null, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default);

    Task TouchAsync(string keyHash, DateTimeOffset usedAt, CancellationToken cancellationToken = default);
}

/// <summary>默认内存 API Key 存储。</summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ConcurrentDictionary<string, ApiKeyRecord> _byHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ApiKeyRecord> _byId = new();

    public Task AddAsync(ApiKeyRecord key, CancellationToken cancellationToken = default)
    {
        _byHash[key.KeyHash] = key;
        _byId[key.Id] = key;
        return Task.CompletedTask;
    }

    public Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byHash.TryGetValue(keyHash, out var key) ? key : null);

    public Task<IReadOnlyList<ApiKeyRecord>> ListAsync(string? tenantId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiKeyRecord>>(
            _byId.Values
                .Where(key => tenantId is null || string.Equals(key.TenantId, tenantId, StringComparison.Ordinal))
                .OrderByDescending(key => key.CreatedAtUtc)
                .ToArray());

    public Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(id, out var key))
            return Task.FromResult(false);

        var revoked = key with { Revoked = true };
        _byId[id] = revoked;
        _byHash[key.KeyHash] = revoked;

        return Task.FromResult(true);
    }

    public Task TouchAsync(string keyHash, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        if (_byHash.TryGetValue(keyHash, out var key))
        {
            var touched = key with { LastUsedAtUtc = usedAt };
            _byHash[keyHash] = touched;
            _byId[key.Id] = touched;
        }

        return Task.CompletedTask;
    }
}

/// <summary>API Key 签发与校验。</summary>
public sealed class ApiKeyService(IApiKeyStore store, ApiKeyManagementOptions options)
{
    public async Task<ApiKeyCreated> CreateAsync(
        string name,
        IReadOnlyCollection<string>? scopes = null,
        string? tenantId = null,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var plaintext = $"{options.Prefix}_{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        var prefix = plaintext.Length <= 14 ? plaintext : plaintext[..14];
        var hash = Hash(plaintext);

        var key = new ApiKeyRecord
        {
            Name = name,
            Prefix = prefix,
            KeyHash = hash,
            TenantId = tenantId,
            Scopes = scopes ?? [],
            ExpiresAtUtc = expiresAtUtc ?? (options.DefaultLifetime is { } lifetime
                ? DateTimeOffset.UtcNow.Add(lifetime)
                : null),
        };

        await store.AddAsync(key, cancellationToken).ConfigureAwait(false);

        return new ApiKeyCreated(key, plaintext);
    }

    /// <summary>校验明文 Key；无效返回 null。</summary>
    public async Task<ApiKeyRecord?> ValidateAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return null;

        var hash = Hash(plaintext);
        var key = await store.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (key is null || !key.IsActive(now))
            return null;

        await store.TouchAsync(hash, now, cancellationToken).ConfigureAwait(false);
        return key;
    }

    public Task<IReadOnlyList<ApiKeyRecord>> ListAsync(string? tenantId = null, CancellationToken cancellationToken = default) =>
        store.ListAsync(tenantId, cancellationToken);

    public Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.RevokeAsync(id, cancellationToken);

    /// <summary>Key 哈希算法：SHA-256 十六进制。</summary>
    public static string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
