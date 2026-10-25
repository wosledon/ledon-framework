using Ledon.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.BlobStorage;

/// <summary>
/// 租户前缀装饰器：多租户部署下把 <c>tenants/{tenantId}/</c> 加到每个路径前，
/// 租户间文件天然隔离（不知道别人文件名就访问不到）。未启用多租户时直通。
/// </summary>
public sealed class TenantPrefixedFileStorage(IFileStorage inner, IServiceProvider services) : IFileStorage
{
    private readonly Func<string?> _tenant = () => services.GetService<ITenantContext>()?.TenantId;

    public Task SaveAsync(string path, Stream content, BlobUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var resolved = BlobPath.WithTenantPrefix(path, _tenant);
        return options is null
            ? inner.SaveAsync(resolved, content, null, cancellationToken)
            : inner.SaveAsync(resolved, content, options, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
        inner.OpenReadAsync(BlobPath.WithTenantPrefix(path, _tenant), cancellationToken);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) =>
        inner.ReadAllBytesAsync(BlobPath.WithTenantPrefix(path, _tenant), cancellationToken);

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(BlobPath.WithTenantPrefix(path, _tenant), cancellationToken);

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(BlobPath.WithTenantPrefix(path, _tenant), cancellationToken);

    public Task<BlobInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        inner.GetInfoAsync(BlobPath.WithTenantPrefix(path, _tenant), cancellationToken);

    public Task<IReadOnlyList<BlobInfo>> ListAsync(
        string? prefix = null, int pageSize = 100, string? continuationToken = null, CancellationToken cancellationToken = default) =>
        inner.ListAsync(
            prefix is null ? null : BlobPath.WithTenantPrefix(prefix, _tenant),
            pageSize,
            continuationToken,
            cancellationToken);
}
