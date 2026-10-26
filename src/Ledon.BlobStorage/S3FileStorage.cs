using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Ledon.BlobStorage;

/// <summary>
/// S3 兼容对象存储：AWS S3、MinIO、以及兼容 S3 协议的对象存储。
/// <para>
/// 路径即对象 Key；写入前按 <see cref="LedonBlobStorageOptions.MaxFileSize"/> 限流，
/// 未开启覆盖时使用 <c>If-None-Match: *</c> 做条件写入，避免并发覆盖。
/// 桶不存在时自动创建（无权限创建时保留原始 S3 异常）。
/// </para>
/// </summary>
public sealed class S3FileStorage : IFileStorage, IAsyncDisposable
{
    private readonly LedonBlobStorageOptions _options;
    private readonly IAmazonS3 _client;
    private readonly ILogger<S3FileStorage> _logger;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool _bucketReady;

    public S3FileStorage(LedonBlobStorageOptions options, ILogger<S3FileStorage> logger)
        : this(options, logger, CreateClient(options), ownsClient: true)
    {
    }

    /// <summary>注入自定义 <see cref="IAmazonS3"/>（测试/高级凭证配置）。调用方保留所有权。</summary>
    public S3FileStorage(LedonBlobStorageOptions options, ILogger<S3FileStorage> logger, IAmazonS3 client)
        : this(options, logger, client, ownsClient: false)
    {
    }

    private S3FileStorage(
        LedonBlobStorageOptions options,
        ILogger<S3FileStorage> logger,
        IAmazonS3 client,
        bool ownsClient)
    {
        _options = options;
        _logger = logger;
        _client = client;
        _ownsClient = ownsClient;
    }

    public async Task SaveAsync(
        string path, Stream content, BlobUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = BlobPath.Validate(path);
        BlobPath.ValidateExtension(key, _options);

        if (_options.MaxFileSize > 0 && content.CanSeek
            && content.Length - content.Position > _options.MaxFileSize)
        {
            throw new IOException($"文件超过大小上限（{_options.MaxFileSize} 字节）");
        }

        if (!(options?.Overwrite ?? false) && await ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            throw new IOException($"文件已存在：{path}（需要覆盖请传 Overwrite = true）");

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName!,
            Key = key,
            InputStream = new SizeLimitedReadStream(content, _options.MaxFileSize),
            AutoCloseStream = false,
            ContentType = options?.ContentType ?? ContentTypes.Guess(key),

            // MinIO / 部分 S3 兼容实现不支持 aws-chunked 签名：显式关闭流式分块
            UseChunkEncoding = false,
        };

        if (!(options?.Overwrite ?? false))
            request.IfNoneMatch = "*";

        if (options?.Metadata is { Count: > 0 })
        {
            foreach (var (name, value) in options.Metadata)
                request.Metadata[name] = value;
        }

        try
        {
            await PutWithBucketAsync(request, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("已写入 S3 对象 {Key}", key);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new IOException($"文件已存在：{path}（需要覆盖请传 Overwrite = true）", ex);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = BlobPath.Validate(path);

        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _options.BucketName!,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);

            return new S3ResponseStream(response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"文件不存在：{path}", path);
        }
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = BlobPath.Validate(path);

        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.BucketName!,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = BlobPath.Validate(path);

        if (!await ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            return false;

        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _options.BucketName!,
            Key = key,
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<BlobInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = BlobPath.Validate(path);

        try
        {
            var metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.BucketName!,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);

            var lastModified = metadata.LastModified ?? DateTime.MinValue;

            return new BlobInfo(
                key,
                metadata.Headers.ContentLength,
                metadata.ContentType,
                new DateTimeOffset(lastModified.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<BlobInfo>> ListAsync(
        string? prefix = null, int pageSize = 100, string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        var normalizedPrefix = (prefix ?? string.Empty).Replace('\\', '/').Trim('/');

        var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _options.BucketName!,
            Prefix = normalizedPrefix,
            MaxKeys = Math.Clamp(pageSize, 1, 1000),
            ContinuationToken = continuationToken,
        }, cancellationToken).ConfigureAwait(false);

        var items = (response.S3Objects ?? [])
            .Select(item => new BlobInfo(
                item.Key,
                item.Size ?? 0,
                ContentTypes.Guess(item.Key),
                item.LastModified is { } modified
                    ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero)
                    : DateTimeOffset.MinValue))
            .ToArray();

        return items;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsClient)
            _client.Dispose();

        _bucketGate.Dispose();
    }

    private async Task PutWithBucketAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            _bucketReady = true;
        }
        catch (AmazonS3Exception ex) when (!_bucketReady && IsMissingBucket(ex))
        {
            await CreateBucketAsync(cancellationToken).ConfigureAwait(false);

            // 重新提交：流被 SDK 读过时可能已到末尾，能 Seek 就回到起点
            if (request.InputStream.CanSeek)
                request.InputStream.Position = 0;

            await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            _bucketReady = true;
        }
    }

    private async Task CreateBucketAsync(CancellationToken cancellationToken)
    {
        await _bucketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bucketReady)
                return;

            try
            {
                await _client.PutBucketAsync(new PutBucketRequest
                {
                    BucketName = _options.BucketName!,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
                // 并发实例已创建：继续走重试
            }
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    private static bool IsMissingBucket(AmazonS3Exception exception) =>
        exception.ErrorCode == "NoSuchBucket" || exception.StatusCode == HttpStatusCode.NotFound;

    private static IAmazonS3 CreateClient(LedonBlobStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BucketName))
            throw new InvalidOperationException("S3 需要配置 BucketName");

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region,
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromSeconds(100),
        };

        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            // 未显式配置服务地址：走 AWS 官方端点
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(config.AuthenticationRegion);
        }
        else
        {
            if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var serviceUri))
                throw new InvalidOperationException($"S3 ServiceUrl 不是合法地址：{options.ServiceUrl}");

            config.ServiceURL = serviceUri.ToString().TrimEnd('/');
            config.UseHttp = serviceUri.Scheme == Uri.UriSchemeHttp;
        }

        AWSCredentials credentials;

        if (!string.IsNullOrEmpty(options.AccessKey))
        {
            credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
        }
        else
        {
            // 未显式配置密钥：使用 AWS 默认凭证链（环境变量 / 配置文件 / 实例角色）
            try
            {
                credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials(config);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "S3 未配置 AccessKey/SecretKey，且无法从默认凭证链获取凭证。" +
                    "请配置访问密钥，或提供环境变量/实例角色。", ex);
            }
        }

        return new AmazonS3Client(credentials, config);
    }

    /// <summary>读取时计数并在超限时立即失败，避免把超大文件完整传给对象存储。</summary>
    private sealed class SizeLimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _readTotal;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                inner.Position = value;
                _readTotal = 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush() => inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _readTotal += read;

            if (maxBytes > 0 && _readTotal > maxBytes)
                throw new IOException($"文件超过大小上限（{maxBytes} 字节）");

            return read;
        }

        protected override void Dispose(bool disposing)
        {
            // 不释放调用方的流：所有权仍在调用方
            base.Dispose(disposing);
        }
    }

    /// <summary>把 S3 响应流的释放与 HTTP 响应绑定，避免连接泄漏。</summary>
    private sealed class S3ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void Flush() => _inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
