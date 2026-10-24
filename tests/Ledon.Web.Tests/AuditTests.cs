using Ledon.Data;
using Ledon.Module.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 数据层约定测试：审计字段自动填充（IAudited）、软删除转换（ISoftDelete）、
/// 查询过滤显式启用、连接弹性接线。实现标记接口即生效，行为由 LedonDataOptions 控制。
/// </summary>
public class AuditTests : IDisposable
{
    private WebApplication? _app;

    private static string NewDbPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, $"audit-{name}-{Guid.NewGuid():N}.db");

    private async Task<WebApplication> StartAsync(string dbPath, Action<LedonDataOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonData(configure);
        builder.Services.AddSingleton<IAuditUserAccessor>(new FixedUserAccessor("user-1"));
        builder.Services.AddDbContext<AuditTestDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return app;
    }

    [Fact]
    public async Task Auditing_fills_timestamps_and_actor_on_create_and_update()
    {
        var app = await StartAsync(NewDbPath("audit"), o => o.EnableAuditing = true);

        Guid id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
            db.Notes.Add(new AuditedNote { Title = "first" });
            await db.SaveChangesAsync();
            id = db.Notes.Single().Id;
        }

        await Task.Delay(20);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
            var note = await db.Notes.SingleAsync();
            Assert.NotEqual(default, note.CreatedAt);
            Assert.Equal("user-1", note.CreatedBy);
            Assert.NotNull(note.ModifiedAt);

            note.Title = "second";
            await db.SaveChangesAsync();

            var updated = await db.Notes.SingleAsync();
            Assert.True(updated.ModifiedAt > updated.CreatedAt, "修改时间应晚于创建时间");
            Assert.Equal("user-1", updated.ModifiedBy);
        }
    }

    [Fact]
    public async Task Auditing_is_off_by_default()
    {
        var app = await StartAsync(NewDbPath("noaudit"));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
            db.Notes.Add(new AuditedNote { Title = "first" });
            await db.SaveChangesAsync();

            var note = await db.Notes.SingleAsync();
            Assert.Equal(default, note.CreatedAt);
            Assert.Null(note.CreatedBy);
        }
    }

    [Fact]
    public async Task Soft_delete_converts_remove_to_update_and_filter_hides_rows()
    {
        var app = await StartAsync(NewDbPath("softdelete"));

        Guid id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
            db.Items.Add(new SoftDeletedItem { Name = "keep" });
            db.Items.Add(new SoftDeletedItem { Name = "remove" });
            await db.SaveChangesAsync();
            id = db.Items.Single(item => item.Name == "remove").Id;
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
            var item = await db.Items.SingleAsync(i => i.Id == id);
            db.Items.Remove(item);
            await db.SaveChangesAsync();
        }

        // 删除被转换为更新：行还在，IsDeleted = true
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();
            var row = await db.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == id);
            Assert.True(row.IsDeleted);

            // 查询过滤（显式 ApplySoftDeleteFilters）隐藏已删除行
            Assert.Single(await db.Items.ToListAsync());
        }
    }

    [Fact]
    public async Task Retry_on_failure_is_a_noop_for_providers_without_the_extension()
    {
        // SQLite 提供程序没有 EnableRetryOnFailure 扩展（不支持瞬断重试）：
        // 开启选项不应报错，执行策略保持非重试——真正的关系型库（SqlServer/Npgsql）会生效
        var app = await StartAsync(NewDbPath("retry"), o => o.EnableRetryOnFailure = true);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();

        Assert.False(db.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    [Fact]
    public void EnableRetryOnFailure_locating_provider_extension_passes_retry_count()
    {
        // 反射机制本身：测试程序集里的桩扩展（签名与真实提供程序一致）被定位并收到重试次数
        FakeProviderExtensions.LastMaxRetryCount = -1;

        var builder = new DbContextOptionsBuilder();
        builder.EnableRetryOnFailure(5);

        Assert.Equal(5, FakeProviderExtensions.LastMaxRetryCount);
    }

    [Fact]
    public async Task Retry_on_failure_is_off_by_default()
    {
        var app = await StartAsync(NewDbPath("noretry"));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditTestDbContext>();

        // 默认不注册重试策略
        Assert.False(db.Database.CreateExecutionStrategy().RetriesOnFailure);
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

public class AuditedNote : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
}

public class SoftDeletedItem : IAudited, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
}

[DisableAutoDiscovery]
public class AuditTestDbContext(DbContextOptions<AuditTestDbContext> options)
    : DbContext(options), ILedonDbContext
{
    public DbSet<AuditedNote> Notes => Set<AuditedNote>();
    public DbSet<SoftDeletedItem> Items => Set<SoftDeletedItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 软删除查询过滤：显式启用（与多租户一致的显式边界）
        modelBuilder.ApplySoftDeleteFilters();
    }
}

/// <summary>固定操作人的审计来源（测试替身）。</summary>
public sealed class FixedUserAccessor(string userId) : IAuditUserAccessor
{
    public string? CurrentUserId => userId;
}

/// <summary>桩提供程序扩展：签名与真实关系型提供程序的 EnableRetryOnFailure 一致，
/// 用于验证 RelationalResilienceExtensions 的反射定位机制。</summary>
public static class FakeProviderExtensions
{
    public static int LastMaxRetryCount { get; set; } = -1;

    public static DbContextOptionsBuilder EnableRetryOnFailure(
        this DbContextOptionsBuilder optionsBuilder, int maxRetryCount)
    {
        LastMaxRetryCount = maxRetryCount;
        return optionsBuilder;
    }
}
