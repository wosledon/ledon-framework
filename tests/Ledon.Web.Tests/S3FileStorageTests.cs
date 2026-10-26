using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using Ledon.BlobStorage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>S3 兼容存储：通过路径风格的最小 S3 HTTP 服务端跑完整契约（PUT/GET/HEAD/DELETE/ListV2）。</summary>
public class S3FileStorageTests : FileStorageContract, IAsyncLifetime
{
    private FakeS3Server _server = null!;

    public async Task InitializeAsync() => _server = await FakeS3Server.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    protected override LedonBlobStorageOptions Options() => new()
    {
        Provider = LedonBlobProvider.S3,
        ServiceUrl = _server.Url,
        BucketName = $"ledon-test-{Guid.NewGuid():N}",   // 每个 Storage 独立桶，契约用例互不干扰
        Region = "us-east-1",
        AccessKey = "test-access-key",
        SecretKey = "test-secret-key",
        ForcePathStyle = true,
        MaxFileSize = 1024,
    };

    protected override IFileStorage CreateStorage(LedonBlobStorageOptions options) =>
        new S3FileStorage(options, NullLogger<S3FileStorage>.Instance);
}

/// <summary>最小 S3 服务端：只实现本框架用到的对象操作，路径风格。</summary>
internal sealed class FakeS3Server : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StoredObject>> _buckets = new();

    private FakeS3Server(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public static async Task<FakeS3Server> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var server = new FakeS3Server(app, string.Empty);

        app.MapPut("/{bucket}", (string bucket) =>
        {
            server._buckets.GetOrAdd(bucket, _ => new ConcurrentDictionary<string, StoredObject>(StringComparer.Ordinal));
            return Results.Ok();
        });

        app.MapPut("/{bucket}/{**key}", async (string bucket, string key, HttpContext context) =>
        {
            if (!server._buckets.TryGetValue(bucket, out var objects))
                return Error(HttpStatusCode.NotFound, "NoSuchBucket");

            if (context.Request.Headers.IfNoneMatch.ToString() == "*" && objects.ContainsKey(key))
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);

            objects[key] = new StoredObject(
                buffer.ToArray(),
                context.Request.ContentType ?? "application/octet-stream",
                DateTime.UtcNow);

            context.Response.Headers.ETag = $"\"{Guid.NewGuid():N}\"";
            return Results.Ok();
        });

        app.MapMethods("/{bucket}/{**key}", ["GET"], (string bucket, string key) =>
        {
            if (!server._buckets.TryGetValue(bucket, out var objects))
                return Error(HttpStatusCode.NotFound, "NoSuchBucket");

            if (!objects.TryGetValue(key, out var stored))
                return Error(HttpStatusCode.NotFound, "NoSuchKey");

            return Results.Bytes(stored.Content, stored.ContentType, lastModified: stored.LastModified);
        });

        app.MapMethods("/{bucket}/{**key}", ["HEAD"], (string bucket, string key, HttpContext context) =>
        {
            if (!server._buckets.TryGetValue(bucket, out var objects))
                return Error(HttpStatusCode.NotFound, "NoSuchBucket");

            if (!objects.TryGetValue(key, out var stored))
                return Error(HttpStatusCode.NotFound, "NoSuchKey");

            context.Response.ContentType = stored.ContentType;
            context.Response.ContentLength = stored.Content.Length;
            context.Response.Headers.LastModified = stored.LastModified.ToString("R");
            return Results.Ok();
        });

        app.MapDelete("/{bucket}/{**key}", (string bucket, string key) =>
        {
            if (!server._buckets.TryGetValue(bucket, out var objects))
                return Error(HttpStatusCode.NotFound, "NoSuchBucket");

            objects.TryRemove(key, out _);
            return Results.NoContent();
        });

        app.MapGet("/{bucket}", (string bucket, HttpContext context) =>
        {
            if (!server._buckets.TryGetValue(bucket, out var objects))
                return Error(HttpStatusCode.NotFound, "NoSuchBucket");

            var prefix = context.Request.Query["prefix"].ToString();
            var maxKeys = int.TryParse(context.Request.Query["max-keys"], out var parsed) ? Math.Clamp(parsed, 1, 1000) : 1000;
            var token = context.Request.Query["continuation-token"].ToString();
            var skip = int.TryParse(token, out var parsedSkip) ? Math.Max(0, parsedSkip) : 0;

            var all = objects
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();

            var page = all.Skip(skip).Take(maxKeys).ToArray();
            var next = skip + page.Length < all.Length ? (skip + page.Length).ToString() : null;

            XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";

            var document = new XElement(ns + "ListBucketResult",
                new XElement(ns + "Name", bucket),
                new XElement(ns + "Prefix", prefix),
                new XElement(ns + "KeyCount", page.Length),
                new XElement(ns + "MaxKeys", maxKeys),
                new XElement(ns + "IsTruncated", next is not null),
                next is null ? null : new XElement(ns + "NextContinuationToken", next),
                page.Select(pair => new XElement(ns + "Contents",
                    new XElement(ns + "Key", pair.Key),
                    new XElement(ns + "LastModified", pair.Value.LastModified.ToString("O")),
                    new XElement(ns + "ETag", $"\"{pair.Key.GetHashCode():x}\""),
                    new XElement(ns + "Size", pair.Value.Content.Length),
                    new XElement(ns + "StorageClass", "STANDARD"))));

            return Results.Content(document.ToString(SaveOptions.DisableFormatting), "application/xml");
        });

        await app.StartAsync();

        var address = app.Urls.First();
        return new FakeS3Server(app, address.TrimEnd('/'));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static IResult Error(HttpStatusCode status, string code) =>
        Results.Content(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>",
            "application/xml",
            statusCode: (int)status);

    private sealed record StoredObject(byte[] Content, string ContentType, DateTime LastModified);
}
