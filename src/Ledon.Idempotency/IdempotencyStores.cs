using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Ledon.Idempotency;

/// <summary>进程内幂等存储（无分布式缓存时的默认实现）。</summary>
public sealed class InMemoryIdempotencyStore(TimeProvider timeProvider) : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc > now)
                return Task.FromResult<IdempotencyRecord?>(entry.Record);

            _entries.TryRemove(key, out _);
        }

        return Task.FromResult<IdempotencyRecord?>(null);
    }

    public Task SaveAsync(
        string key,
        IdempotencyRecord record,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        _entries[key] = new Entry(record, timeProvider.GetUtcNow() + ttl);

        return Task.CompletedTask;
    }

    private sealed record Entry(IdempotencyRecord Record, DateTimeOffset ExpiresAtUtc);
}

/// <summary>基于 IDistributedCache 的幂等存储（多实例共享响应）。</summary>
public sealed class DistributedIdempotencyStore(
    IDistributedCache cache,
    IdempotencyOptions options) : IIdempotencyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await cache.GetAsync(CacheKey(key), cancellationToken).ConfigureAwait(false);

        return bytes is null ? null : JsonSerializer.Deserialize<IdempotencyRecord>(bytes, JsonOptions);
    }

    public Task SaveAsync(
        string key,
        IdempotencyRecord record,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) =>
        cache.SetAsync(
            CacheKey(key),
            JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);

    private string CacheKey(string key) => $"{options.StoragePrefix}:{key}";
}
