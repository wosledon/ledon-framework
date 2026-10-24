using Ledon.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 多租户查询过滤测试：<c>IMultiTenant</c> 标记 + <c>ApplyTenantFilters</c> 显式启用，
/// 与软删除过滤同一设计边界（过滤显式声明，不隐式生效）。
/// </summary>
public class TenantFilterTests : IDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync(bool filterWhenNoTenant = false)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"tenant-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonMultiTenancy();
        builder.Services.AddDbContext<TenantTestDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenantTestDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return app;
    }

    [Theory]
    [InlineData("tenant-a", 1)]
    [InlineData("tenant-b", 1)]
    public async Task Filter_returns_only_current_tenant_rows(string tenantId, int expected)
    {
        var app = await StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenantTestDbContext>();
            db.Tenants.Add(new TenantNote { TenantId = "tenant-a", Text = "a" });
            db.Tenants.Add(new TenantNote { TenantId = "tenant-b", Text = "b" });
            await db.SaveChangesAsync();
        }

        // 每作用域设置当前租户（真实场景：Web 中间件写入 / 后台 SetAmbient 穿透后写入）
        using (TenantContext.SetAmbient(tenantId))
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TenantTestDbContext>();
            db.CurrentTenantId = tenantId;

            var rows = await db.Tenants.ToListAsync();
            Assert.Equal(expected, rows.Count);
            Assert.All(rows, row => Assert.Equal(tenantId, row.TenantId));
        }
    }

    [Fact]
    public async Task No_tenant_context_sees_all_rows_by_default()
    {
        var app = await StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenantTestDbContext>();
            db.Tenants.Add(new TenantNote { TenantId = "tenant-a", Text = "a" });
            db.Tenants.Add(new TenantNote { TenantId = "tenant-b", Text = "b" });
            await db.SaveChangesAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenantTestDbContext>();

            // 默认（filterWhenNoTenant = false）：无租户上下文不过滤——宿主/后台穿透视角
            Assert.Equal(2, await db.Tenants.CountAsync());
        }
    }

    [Fact]
    public async Task No_tenant_context_sees_nothing_when_strict()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"tenant-strict-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonMultiTenancy();
        builder.Services.AddDbContext<StrictTenantDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrictTenantDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new TenantNote { TenantId = "tenant-a", Text = "a" });
            await db.SaveChangesAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrictTenantDbContext>();

            // 严格模式：未解析出租户的上下文看不到任何行
            Assert.Equal(0, await db.Tenants.CountAsync());
        }
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}

// ---------- 夹具 ----------

public class TenantNote : IMultiTenant
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>默认过滤语义的测试上下文（上下文成员模式）。</summary>
public class TenantTestDbContext(DbContextOptions<TenantTestDbContext> options) : DbContext(options)
{
    /// <summary>当前租户（每作用域设置）。</summary>
    public string? CurrentTenantId { get; set; }

    public DbSet<TenantNote> Tenants => Set<TenantNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyTenantFilters(this, context => context.CurrentTenantId);
}

/// <summary>严格模式（无租户时过滤为空）的测试上下文。</summary>
public class StrictTenantDbContext(DbContextOptions<StrictTenantDbContext> options) : DbContext(options)
{
    /// <summary>当前租户（每作用域设置）。</summary>
    public string? CurrentTenantId { get; set; }

    public DbSet<TenantNote> Tenants => Set<TenantNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyTenantFilters(this, context => context.CurrentTenantId, filterWhenNoTenant: true);
}
