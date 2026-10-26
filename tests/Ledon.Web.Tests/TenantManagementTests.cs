using System.Net;
using System.Net.Http.Json;
using Ledon.MultiTenancy;
using Ledon.MultiTenancy.Management;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>租户管理：CRUD、重复校验、开通/回收钩子与回滚、管理端点。</summary>
public class TenantManagementTests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Service_crud_and_provisioning()
    {
        var store = new InMemoryTenantStore();
        var provisioner = new RecordingProvisioner();
        var service = new TenantManagementService(store, [provisioner], new AllowAllValidator());

        var tenant = await service.CreateAsync("tenant-a", "Tenant A", "Data Source=a.db");
        Assert.Equal("tenant-a", tenant.Id);
        Assert.Single(provisioner.Provisioned);
        Assert.Single(await service.ListAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("tenant-a"));

        await service.UpdateAsync("tenant-a", "Tenant A2", null, active: false);
        var updated = await service.FindAsync("tenant-a");
        Assert.NotNull(updated);
        Assert.Equal("Tenant A2", updated.Name);
        Assert.False(updated.Active);

        Assert.True(await service.DeleteAsync("tenant-a"));
        Assert.Empty(await service.ListAsync());
        Assert.Single(provisioner.Deprovisioned);

        // 开通失败回滚目录项
        var failing = new TenantManagementService(store, [new FailingProvisioner()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CreateAsync("tenant-b"));
        Assert.Null(await store.FindAsync("tenant-b"));
    }

    [Fact]
    public async Task Endpoints_manage_tenants()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonTenantManagement();

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();
        _app = app;

        var client = app.GetTestClient();

        var created = await client.PostAsJsonAsync("/tenants", new { id = "t1", name = "Tenant 1" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await client.GetAsync("/tenants");
        list.EnsureSuccessStatusCode();
        Assert.Contains("t1", await list.Content.ReadAsStringAsync());

        var deleted = await client.DeleteAsync("/tenants/t1");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var missing = await client.GetAsync("/tenants/t1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class AllowAllValidator : ITenantValidator
    {
        public ValueTask<bool> IsValidAsync(string tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class RecordingProvisioner : ITenantProvisioner
    {
        public List<string> Provisioned { get; } = [];
        public List<string> Deprovisioned { get; } = [];

        public Task ProvisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
        {
            Provisioned.Add(tenant.Id);
            return Task.CompletedTask;
        }

        public Task DeprovisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default)
        {
            Deprovisioned.Add(tenant.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingProvisioner : ITenantProvisioner
    {
        public Task ProvisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provision failed");

        public Task DeprovisionAsync(TenantInfo tenant, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
