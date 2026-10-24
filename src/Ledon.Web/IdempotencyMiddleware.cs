using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Web;

/// <summary>
/// 幂等中间件：带 <c>Idempotency-Key</c> 请求头的变更请求（POST/PUT/PATCH）按
/// (方法 + 路径 + Key) 缓存响应；重放直接返回缓存（带 <c>Idempotency-Replayed: true</c> 头），
/// 并发同 Key 只执行一次（进程内单飞，等首个执行完后取缓存）。
/// 只缓存 2xx 响应——失败不占键，客户端可带同 Key 重试。
/// 需要 IDistributedCache 在场（<c>AddDistributedMemoryCache</c> 或 <c>AddLedonDistributedCaching</c>）；
/// 未注册时中间件透明跳过。
/// </summary>
internal sealed class IdempotencyMiddleware
{
    internal const string HeaderName = "Idempotency-Key";
    private const string ReplayedHeaderName = "Idempotency-Replayed";

    // 进程内单飞闸门（同 key 并发只放一个进去执行）；跨实例去重由 IDistributedCache 承担
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    private readonly RequestDelegate _next;
    private readonly LedonWebOptions _options;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        LedonWebOptions options,
        ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var key = context.Request.Headers[HeaderName].FirstOrDefault();

        var cache = context.RequestServices.GetService<IDistributedCache>();
        if (cache is null || string.IsNullOrWhiteSpace(key) || !IsMutating(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var cacheKey = BuildCacheKey(context.Request.Method, context.Request.Path, key);

        // 重放：命中即返回缓存响应
        var cached = await ReadAsync(cache, cacheKey, context.RequestAborted);
        if (cached is not null)
        {
            _logger.LogDebug("幂等重放（缓存命中）：{Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, cached);
            return;
        }

        // 并发同 Key：等首个执行完（进程内单飞）
        var gate = Gates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(context.RequestAborted);

        try
        {
            // 等闸期间可能已被首个执行方写入
            cached = await ReadAsync(cache, cacheKey, context.RequestAborted);
            if (cached is not null)
            {
                _logger.LogDebug("幂等重放（等待并发执行方后命中）：{Method} {Path}", context.Request.Method, context.Request.Path);
                await WriteAsync(context, cached);
                return;
            }

            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            var body = buffer.ToArray();

            // 只缓存成功响应
            if (context.Response.StatusCode is >= 200 and < 300)
            {
                await cache.SetAsync(
                    cacheKey,
                    JsonSerializer.SerializeToUtf8Bytes(new CachedResponse(
                        context.Response.StatusCode,
                        context.Response.ContentType,
                        body)),
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = _options.IdempotencyKeyTtl,
                    },
                    context.RequestAborted);
            }

            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
        finally
        {
            gate.Release();

            // 无竞争时清理闸门，避免字典随 key 无限增长
            if (gate.CurrentCount == 1)
                Gates.TryRemove(cacheKey, out _);
        }
    }

    private static bool IsMutating(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

    private static string BuildCacheKey(string method, string path, string key)
    {
        // 客户端 key 直接入缓存键；超长 key 先哈希（Redis key 不宜超长）
        var raw = $"{method}:{path}:{key}";
        var hashed = raw.Length > 256
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            : raw;

        return $"idempotency:{hashed}";
    }

    private static async Task<CachedResponse?> ReadAsync(
        IDistributedCache cache, string cacheKey, CancellationToken cancellationToken)
    {
        var bytes = await cache.GetAsync(cacheKey, cancellationToken);
        return bytes is null ? null : JsonSerializer.Deserialize<CachedResponse>(bytes);
    }

    private static async Task WriteAsync(HttpContext context, CachedResponse cached)
    {
        context.Response.StatusCode = cached.Status;
        context.Response.ContentType = cached.ContentType;
        context.Response.Headers[ReplayedHeaderName] = "true";
        await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted);
    }

    private sealed record CachedResponse(int Status, string? ContentType, byte[] Body);
}
