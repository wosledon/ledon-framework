using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ledon.Locking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Idempotency;

/// <summary>
/// 生产级幂等中间件：Idempotency-Key + 请求指纹 + 响应重放 + 同键冲突 + 并发去重。
/// 多实例建议同时注册 Redis 分布式锁（<c>AddLedonLocking(o =&gt; o.Provider = Redis)</c>）。
/// </summary>
public sealed class IdempotencyMiddleware(
    RequestDelegate next,
    IdempotencyOptions options,
    IIdempotencyStore store,
    TimeProvider timeProvider)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Methods.Contains(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var key = context.Request.Headers[options.HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(key))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (key.Length > options.MaxKeyLength)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "idempotency_key_invalid", "Idempotency-Key 过长");
            return;
        }

        var fingerprint = await ComputeFingerprintAsync(context, options).ConfigureAwait(false);

        if (fingerprint is null)
            return;

        var partition = options.PartitionKeySelector(context);
        var cacheKey = StorageKey(context.Request.Method, context.Request.Path, key, partition);
        var lockKey = $"ledon:idempotency:{cacheKey}";

        var distributedLock = context.RequestServices.GetService<IDistributedLock>();
        ILockHandle? lockHandle = null;
        SemaphoreSlim? gate = null;

        try
        {
            if (distributedLock is not null)
            {
                lockHandle = await distributedLock
                    .TryAcquireAsync(lockKey, options.LockExpiry, context.RequestAborted)
                    .ConfigureAwait(false);

                if (lockHandle is null)
                {
                    await WriteProblemAsync(context, options.ConflictStatusCode, "idempotency_in_progress", "相同幂等键的请求正在处理中");
                    return;
                }
            }
            else
            {
                gate = Gates.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

                if (!await gate.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted).ConfigureAwait(false))
                {
                    await WriteProblemAsync(context, options.ConflictStatusCode, "idempotency_in_progress", "相同幂等键的请求正在处理中");
                    return;
                }
            }

            var existing = await store.GetAsync(cacheKey, context.RequestAborted).ConfigureAwait(false);

            if (existing is not null)
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    await WriteConflictAsync(context, options);
                    return;
                }

                await WriteRecordAsync(context, existing, replay: true, options).ConfigureAwait(false);
                return;
            }

            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();

            try
            {
                context.Response.Body = buffer;
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            var body = buffer.ToArray();

            if (options.CacheableStatus(context.Response.StatusCode) && body.LongLength <= options.MaxResponseBodyBytes)
            {
                var record = new IdempotencyRecord(
                    fingerprint,
                    context.Response.StatusCode,
                    context.Response.ContentType,
                    body,
                    CaptureHeaders(context.Response, options),
                    timeProvider.GetUtcNow());

                await store.SaveAsync(cacheKey, record, options.Ttl, context.RequestAborted).ConfigureAwait(false);
            }

            await WriteBodyAsync(context, context.Response.StatusCode, context.Response.ContentType, body).ConfigureAwait(false);
        }
        finally
        {
            if (lockHandle is not null)
                await lockHandle.DisposeAsync().ConfigureAwait(false);

            if (gate is not null)
            {
                gate.Release();

                if (gate.CurrentCount == 1)
                    Gates.TryRemove(lockKey, out _);
            }
        }
    }

    private static async Task<string?> ComputeFingerprintAsync(HttpContext context, IdempotencyOptions options)
    {
        var request = context.Request;
        request.EnableBuffering();

        using var body = new MemoryStream();

        if (request.Body.CanRead)
        {
            await request.Body.CopyToAsync(
                body,
                options.MaxRequestBodyBytes <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, options.MaxRequestBodyBytes + 1),
                context.RequestAborted).ConfigureAwait(false);
        }

        request.Body.Position = 0;

        if (options.MaxRequestBodyBytes > 0 && body.Length > options.MaxRequestBodyBytes)
        {
            await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "idempotency_body_too_large", "请求体超过幂等指纹大小限制");
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(request.Method));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(request.Path.Value ?? string.Empty));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(request.QueryString.Value ?? string.Empty));
        hash.AppendData([0]);
        hash.AppendData(body.GetBuffer().AsSpan(0, (int)body.Length));

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string StorageKey(string method, PathString path, string key, string? partition)
    {
        var raw = $"{partition ?? "-"}:{method}:{path.Value}:{key}";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static Dictionary<string, string> CaptureHeaders(HttpResponse response, IdempotencyOptions options)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in options.ReplayHeaders)
        {
            if (response.Headers.TryGetValue(name, out var value) && value.Count > 0)
                headers[name] = value.ToString();
        }

        return headers;
    }

    private static async Task WriteRecordAsync(
        HttpContext context,
        IdempotencyRecord record,
        bool replay,
        IdempotencyOptions options)
    {
        context.Response.StatusCode = record.StatusCode;

        foreach (var (name, value) in record.Headers)
            context.Response.Headers[name] = value;

        if (replay)
            context.Response.Headers[options.ReplayHeaderName] = "true";

        await WriteBodyAsync(context, record.StatusCode, record.ContentType, record.Body).ConfigureAwait(false);
    }

    private static async Task WriteBodyAsync(
        HttpContext context,
        int statusCode,
        string? contentType,
        byte[] body)
    {
        context.Response.StatusCode = statusCode;

        if (contentType is not null)
            context.Response.ContentType = contentType;

        if (body.Length > 0)
            await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteConflictAsync(HttpContext context, IdempotencyOptions options)
    {
        context.Response.Headers[options.ConflictHeaderName] = "true";
        return WriteProblemAsync(context, options.ConflictStatusCode, "idempotency_key_reused", "同一幂等键对应了不同的请求内容");
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                type = "about:blank",
                title = message,
                status = statusCode,
                code,
                traceId = context.TraceIdentifier,
            },
            cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>幂等模块注册与中间件扩展。</summary>
public static class LedonIdempotencyServiceCollectionExtensions
{
    public static IServiceCollection AddLedonIdempotency(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new IdempotencyOptions();
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.HeaderName))
            throw new InvalidOperationException("IdempotencyOptions.HeaderName 不能为空");

        if (options.Ttl <= TimeSpan.Zero || options.LockExpiry <= TimeSpan.Zero)
            throw new InvalidOperationException("IdempotencyOptions 的 Ttl/LockExpiry 必须大于 0");

        if (options.MaxKeyLength < 1)
            throw new InvalidOperationException("IdempotencyOptions.MaxKeyLength 至少为 1");

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdempotencyStore>(provider =>
        {
            var cache = provider.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();

            return cache is null
                ? new InMemoryIdempotencyStore(provider.GetRequiredService<TimeProvider>())
                : new DistributedIdempotencyStore(cache, options);
        });

        return services;
    }

    /// <summary>启用幂等中间件（在路由之后、端点执行之前）。</summary>
    public static IApplicationBuilder UseLedonIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
