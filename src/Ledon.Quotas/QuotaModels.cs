namespace Ledon.Quotas;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;

/// <summary>配额选项。</summary>
public sealed class QuotaOptions
{
    /// <summary>默认每窗口请求上限，默认 100。</summary>
    public int DefaultLimit { get; set; } = 100;

    /// <summary>默认窗口，默认 1 分钟。</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>是否使用 <c>IDistributedCache</c> 作为存储（多实例），默认 <c>false</c>。</summary>
    public bool UseDistributedCache { get; set; }

    /// <summary>缓存键前缀，默认 <c>ledon:quota</c>。</summary>
    public string KeyPrefix { get; set; } = "ledon:quota";

    /// <summary>配额键选择器；默认按客户端 IP。可按租户/API Key/用户自定义。</summary>
    public Func<HttpContext, string> KeySelector { get; set; } =
        context => context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}

/// <summary>一次配额消费结果。</summary>
public sealed record QuotaDecision(bool Allowed, int Remaining, DateTimeOffset ResetAtUtc);

/// <summary>配额计数器。</summary>
public sealed record QuotaCounter(int Count, DateTimeOffset ResetAtUtc);

/// <summary>配额存储。</summary>
public interface IQuotaStore
{
    Task<QuotaCounter> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default);
}

/// <summary>进程内固定窗口配额存储。</summary>
public sealed class InMemoryQuotaStore(TimeProvider timeProvider) : IQuotaStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CounterEntry> _counters =
        new(StringComparer.Ordinal);

    private long _lastCleanupTicks;

    public Task<QuotaCounter> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var windowTicks = Math.Max(1, window.Ticks);
        var windowId = now.UtcTicks / windowTicks;
        var reset = new DateTimeOffset((windowId + 1) * windowTicks, TimeSpan.Zero);

        var counter = _counters.AddOrUpdate(
            key,
            _ => new CounterEntry(windowId, reset, 1),
            (_, existing) => existing.WindowId == windowId
                ? existing with { Count = existing.Count + 1 }
                : new CounterEntry(windowId, reset, 1));

        CleanupIfDue(now);

        return Task.FromResult(new QuotaCounter(counter.Count, counter.ResetAtUtc));
    }

    private void CleanupIfDue(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastCleanupTicks);

        if (now.UtcTicks - last < TimeSpan.FromMinutes(1).Ticks)
            return;

        if (Interlocked.CompareExchange(ref _lastCleanupTicks, now.UtcTicks, last) != last)
            return;

        foreach (var (key, entry) in _counters)
        {
            if (entry.ResetAtUtc <= now)
                _counters.TryRemove(key, out _);
        }
    }

    private sealed record CounterEntry(long WindowId, DateTimeOffset ResetAtUtc, int Count);
}

/// <summary>基于 IDistributedCache 的固定窗口配额存储（多实例；读改写，非原子）。</summary>
public sealed class DistributedQuotaStore(
    IDistributedCache cache,
    QuotaOptions options) : IQuotaStore
{
    public async Task<QuotaCounter> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var windowTicks = Math.Max(1, window.Ticks);
        var windowId = now.UtcTicks / windowTicks;
        var cacheKey = $"{options.KeyPrefix}:{key}:{windowId}";

        var current = await cache.GetStringAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var count = int.TryParse(current, out var parsed) ? parsed + 1 : 1;
        var reset = new DateTimeOffset((windowId + 1) * windowTicks, TimeSpan.Zero);
        var ttl = reset - now;

        if (ttl > TimeSpan.Zero)
        {
            await cache.SetStringAsync(
                cacheKey,
                count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl,
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new QuotaCounter(count, reset);
    }
}

/// <summary>配额服务：固定窗口计数 + 允许/剩余判断。</summary>
public sealed class QuotaService(IQuotaStore store)
{
    public async Task<QuotaDecision> ConsumeAsync(
        string key,
        int limit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (limit <= 0)
            return new QuotaDecision(false, 0, DateTimeOffset.UtcNow.Add(window));

        var counter = await store.IncrementAsync(key, window, cancellationToken).ConfigureAwait(false);
        var remaining = Math.Max(0, limit - counter.Count);

        return new QuotaDecision(counter.Count <= limit, remaining, counter.ResetAtUtc);
    }
}
