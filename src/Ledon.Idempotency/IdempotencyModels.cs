namespace Ledon.Idempotency;

using Microsoft.AspNetCore.Http;

/// <summary>幂等选项。</summary>
public sealed class IdempotencyOptions
{
    /// <summary>幂等键请求头，默认 Idempotency-Key。</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>响应缓存时长，默认 24 小时。</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>存储键前缀，默认 ledon:idempotency。</summary>
    public string StoragePrefix { get; set; } = "ledon:idempotency";

    /// <summary>处理中锁的时长，默认 30 秒；超过仍未完成时应调大。</summary>
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>最大幂等键长度，默认 255。</summary>
    public int MaxKeyLength { get; set; } = 255;

    /// <summary>最大请求体（指纹计算），默认 1 MB；超过返回 413。</summary>
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>最大缓存响应体，默认 1 MB；超过则不缓存（不影响本次响应）。</summary>
    public long MaxResponseBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>视为可缓存的响应状态码，默认 200-299。</summary>
    public Func<int, bool> CacheableStatus { get; set; } = status => status is >= 200 and < 300;

    /// <summary>同键不同请求体/参数时的冲突状态码，默认 409。</summary>
    public int ConflictStatusCode { get; set; } = StatusCodes.Status409Conflict;

    /// <summary>幂等冲突标记响应头，默认 Idempotency-Conflict。</summary>
    public string ConflictHeaderName { get; set; } = "Idempotency-Conflict";

    /// <summary>重放标记响应头，默认 Idempotency-Replayed。</summary>
    public string ReplayHeaderName { get; set; } = "Idempotency-Replayed";

    /// <summary>参与幂等的 HTTP 方法，默认 POST/PUT/PATCH/DELETE。</summary>
    public HashSet<string> Methods { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
    };

    /// <summary>缓存并在重放时恢复的响应头（除 Content-Type）。</summary>
    public HashSet<string> ReplayHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Location",
        "ETag",
    };

    /// <summary>
    /// 幂等键分区（默认租户头 + 用户 ID）：不同租户/用户使用相同 key 时互不影响。
    /// </summary>
    public Func<HttpContext, string?> PartitionKeySelector { get; set; } = context =>
        context.Request.Headers["X-Tenant-Id"].FirstOrDefault()
        ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
}

/// <summary>已保存的幂等响应。</summary>
public sealed record IdempotencyRecord(
    string Fingerprint,
    int StatusCode,
    string? ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset CreatedAtUtc);

/// <summary>幂等响应存储。</summary>
public interface IIdempotencyStore
{
    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string key,
        IdempotencyRecord record,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}
