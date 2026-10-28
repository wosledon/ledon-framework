using Ledon.MultiTenancy.Management;

namespace Ledon.Web.Tests;

/// <summary>租户管理边界：重复创建、更新缺失、删除未知、开通失败回滚。</summary>
public class TenantManagementEdgeTests
{
    private static TenantManagementService Build(params ITenantProvisioner[] provisioners) =>
        new(new InMemoryTenantStore(), provisioners);

    [Fact]
    public async Task Create_rejects_duplicate_tenant()
    {
        var service = Build();
        await service.CreateAsync("tenant-a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("tenant-a"));
    }

    [Fact]
    public async Task Update_missing_tenant_throws()
    {
        var service = Build();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.UpdateAsync("missing", "name", null, active: true));
    }

    [Fact]
    public async Task Delete_unknown_tenant_returns_false()
    {
        var service = Build();

        Assert.False(await service.DeleteAsync("missing"));
    }

    [Fact]
    public async Task Provisioner_failure_rolls_back_tenant()
    {
        var service = Build(new FailingProvisioner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("tenant-fail"));
        Assert.Null(await service.FindAsync("tenant-fail"));
    }

    private sealed class FailingProvisioner : ITenantProvisioner
    {
        public Task ProvisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provision failed");

        public Task DeprovisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
