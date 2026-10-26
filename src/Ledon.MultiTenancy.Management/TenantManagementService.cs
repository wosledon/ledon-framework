using System.Collections.Concurrent;

namespace Ledon.MultiTenancy.Management;

/// <summary>租户目录项。</summary>
public sealed class TenantInfo
{
    /// <summary>租户标识（URL/查询串中使用）。</summary>
    public required string Id { get; set; }

    /// <summary>展示名。</summary>
    public string? Name { get; set; }

    /// <summary>租户数据库连接串（可选；敏感信息建议加密存储）。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>是否启用。</summary>
    public bool Active { get; set; } = true;

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>租户存储（默认内存；可替换为数据库实现）。</summary>
public interface ITenantStore
{
    Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<TenantInfo?> FindAsync(string tenantId, CancellationToken cancellationToken = default);

    Task AddAsync(TenantInfo tenant, CancellationToken cancellationToken = default);

    Task UpdateAsync(TenantInfo tenant, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>默认内存租户目录。</summary>
public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<string, TenantInfo> _tenants = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantInfo>>(
            _tenants.Values.OrderBy(tenant => tenant.Id, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<TenantInfo?> FindAsync(string tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tenants.TryGetValue(tenantId, out var tenant) ? tenant : null);

    public Task AddAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
    {
        _tenants[tenant.Id] = tenant;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
    {
        _tenants[tenant.Id] = tenant;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tenants.TryRemove(tenantId, out _));
}

/// <summary>租户开通/回收钩子（建库、初始化种子、清理资源等）。</summary>
public interface ITenantProvisioner
{
    Task ProvisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default);

    Task DeprovisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default);
}

/// <summary>租户管理服务。</summary>
public sealed class TenantManagementService(
    ITenantStore store,
    IEnumerable<ITenantProvisioner> provisioners,
    ITenantValidator? validator = null)
{
    public Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    public Task<TenantInfo?> FindAsync(string tenantId, CancellationToken cancellationToken = default) =>
        store.FindAsync(tenantId, cancellationToken);

    public async Task<TenantInfo> CreateAsync(
        string tenantId,
        string? name = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (await store.FindAsync(tenantId, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"租户已存在：{tenantId}");

        if (validator is not null && !await validator.IsValidAsync(tenantId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"租户标识非法：{tenantId}");

        var tenant = new TenantInfo
        {
            Id = tenantId,
            Name = name,
            ConnectionString = connectionString,
        };

        await store.AddAsync(tenant, cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var provisioner in provisioners)
                await provisioner.ProvisionAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.RemoveAsync(tenantId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return tenant;
    }

    public async Task<TenantInfo> UpdateAsync(
        string tenantId,
        string? name,
        string? connectionString,
        bool active,
        CancellationToken cancellationToken = default)
    {
        var tenant = await store.FindAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"租户不存在：{tenantId}");

        tenant.Name = name;
        tenant.ConnectionString = connectionString;
        tenant.Active = active;

        await store.UpdateAsync(tenant, cancellationToken).ConfigureAwait(false);

        foreach (var provisioner in provisioners)
            await provisioner.ProvisionAsync(tenant, cancellationToken).ConfigureAwait(false);

        return tenant;
    }

    public async Task<bool> DeleteAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await store.FindAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is null)
            return false;

        foreach (var provisioner in provisioners)
            await provisioner.DeprovisionAsync(tenant, cancellationToken).ConfigureAwait(false);

        return await store.RemoveAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }
}
