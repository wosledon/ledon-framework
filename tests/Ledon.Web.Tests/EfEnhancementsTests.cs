using Ledon.Core.Events;
using Ledon.Data;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// EF 增强测试：领域事件自动分发、规约查询、分页/动态排序、通用仓储。
/// </summary>
public class EfEnhancementsTests : IDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync(
        Action<LedonDataOptions>? configureData = null,
        bool withRepositories = false)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"ef-enh-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonData(configureData);
        if (withRepositories)
            builder.Services.AddLedonRepositories();
        builder.Services.AddDbContext<EnhancementDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return app;
    }

    // ---------- 领域事件 ----------

    [Fact]
    public async Task Domain_events_are_dispatched_after_save_changes()
    {
        DomainEventSignal.Received.Clear();
        var app = await StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
            var widget = new Widget { Name = "w1" };
            widget.Created();
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
        }

        // SaveChanges 成功后事件被分发（处理器经约定自动注册）
        Assert.Contains("WidgetCreated:w1", DomainEventSignal.Received);

        // 事件已清空：再次保存不会重复分发
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
            var widget = await db.Widgets.SingleAsync();
            await db.SaveChangesAsync();
        }

        Assert.Single(DomainEventSignal.Received);
    }

    [Fact]
    public async Task Domain_events_not_dispatched_when_option_off()
    {
        DomainEventSignal.Received.Clear();
        var app = await StartAsync(o => o.DispatchDomainEvents = false);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
            var widget = new Widget { Name = "w-off" };
            widget.Created();
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
        }

        Assert.Empty(DomainEventSignal.Received);
    }

    // ---------- 规约 / 分页 / 排序 ----------

    private async Task SeedAsync(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
        db.Widgets.AddRange(
            new Widget { Name = "alpha", Price = 30 },
            new Widget { Name = "beta", Price = 10 },
            new Widget { Name = "gamma", Price = 20 },
            new Widget { Name = "delta", Price = 40 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Specification_filters_and_sorts()
    {
        var app = await StartAsync();
        await SeedAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();

        var spec = new CheapWidgetsSpecification();
        var cheap = await db.Widgets.ApplySpecification(spec).ToListAsync();

        Assert.Equal(new[] { "beta", "gamma" }, cheap.Select(w => w.Name));
    }

    [Fact]
    public async Task Paged_result_reports_totals()
    {
        var app = await StartAsync();
        await SeedAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();

        var page1 = await db.Widgets.OrderBy(w => w.Name).ToPagedResultAsync(1, 2);
        var page2 = await db.Widgets.OrderBy(w => w.Name).ToPagedResultAsync(2, 2);

        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.True(page1.HasNext);
        Assert.False(page1.HasPrevious);
        Assert.Equal(new[] { "alpha", "beta" }, page1.Items.Select(w => w.Name));
        Assert.Equal(new[] { "delta", "gamma" }, page2.Items.Select(w => w.Name));
        Assert.True(page2.HasPrevious);
        Assert.False(page2.HasNext);
    }

    [Theory]
    [InlineData("Name", new[] { "alpha", "beta", "delta", "gamma" })]
    [InlineData("Name desc", new[] { "gamma", "delta", "beta", "alpha" })]
    [InlineData("Price", new[] { "beta", "gamma", "alpha", "delta" })]
    public async Task Dynamic_ordering_by_field_name(string sortBy, string[] expected)
    {
        var app = await StartAsync();
        await SeedAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();

        var names = await db.Widgets.ApplyOrdering(sortBy).Select(w => w.Name).ToListAsync();

        Assert.Equal(expected, names);
    }

    [Fact]
    public async Task Dynamic_ordering_falls_back_for_unknown_field()
    {
        var app = await StartAsync();
        await SeedAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();

        // 无效字段名不抛（500），退化为默认排序
        var names = await db.Widgets
            .ApplyOrdering("NoSuchField", w => w.Name)
            .Select(w => w.Name)
            .ToListAsync();

        Assert.Equal(new[] { "alpha", "beta", "delta", "gamma" }, names);
    }

    // ---------- 仓储 ----------

    [Fact]
    public async Task Repository_crud_and_queries()
    {
        var app = await StartAsync(withRepositories: true);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget>>();
            await repo.AddAsync(new Widget { Name = "repo-1", Price = 5 });
            await repo.SaveChangesAsync();
        }

        await using var scope2 = app.Services.CreateAsyncScope();
        var repository = scope2.ServiceProvider.GetRequiredService<IRepository<Widget>>();
        var context = scope2.ServiceProvider.GetRequiredService<EnhancementDbContext>();

        Assert.Equal(1, await repository.CountAsync());
        Assert.True(await repository.AnyAsync(w => w.Name == "repo-1"));

        var found = await repository.FirstOrDefaultAsync(w => w.Name == "repo-1");
        Assert.NotNull(found);
        Assert.Equal(5, found.Price);
        Assert.Equal(EntityState.Detached, context.Entry(found).State);   // 默认不跟踪

        var listed = await repository.ListAsync(new CheapWidgetsSpecification());
        Assert.Single(listed);
        Assert.Equal(EntityState.Detached, context.Entry(listed[0]).State);

        var paged = await repository.PagedAsync(1, 10);
        Assert.Single(paged.Items);
        Assert.Equal(EntityState.Detached, context.Entry(paged.Items[0]).State);
    }

    [Fact]
    public async Task Repository_get_by_id_queries_database_and_supports_no_tracking()
    {
        var app = await StartAsync(withRepositories: true);
        int id;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
            var widget = new Widget { Name = "lookup", Price = 7 };
            db.Widgets.Add(widget);
            await db.SaveChangesAsync();
            id = widget.Id;
        }

        await using var scope2 = app.Services.CreateAsyncScope();
        var repository = scope2.ServiceProvider.GetRequiredService<IRepository<Widget>>();
        var context = scope2.ServiceProvider.GetRequiredService<EnhancementDbContext>();

        // 新作用域：实体没有被追踪；读接口默认 AsNoTracking，直接查库返回 Detached
        var untracked = await repository.GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(untracked);
        Assert.Equal(EntityState.Detached, context.Entry(untracked).State);

        // 需要修改时显式走跟踪版本
        var tracked = await repository.GetByIdForUpdateAsync(id, CancellationToken.None);
        Assert.NotNull(tracked);
        Assert.Equal(EntityState.Unchanged, context.Entry(tracked).State);
    }

    [Fact]
    public async Task Repository_get_by_id_respects_soft_delete_filter()
    {
        var app = await StartAsync(withRepositories: true);
        int id;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
            var widget = new SoftWidget { Name = "deleted" };
            db.SoftWidgets.Add(widget);
            await db.SaveChangesAsync();
            id = widget.Id;

            db.SoftWidgets.Remove(widget);   // AutoSoftDelete 转换为 IsDeleted = true
            await db.SaveChangesAsync();
        }

        await using var scope2 = app.Services.CreateAsyncScope();
        var repository = scope2.ServiceProvider.GetRequiredService<IRepository<SoftWidget>>();

        // 查询过滤器生效：软删除记录按主键也查不到（FindAsync 在已追踪时可能绕过过滤器）
        Assert.Null(await repository.GetByIdAsync(id, CancellationToken.None));
        Assert.Null(await repository.GetByIdForUpdateAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Repository_supports_composite_keys()
    {
        var app = await StartAsync(withRepositories: true);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EnhancementDbContext>();
            db.WidgetLines.Add(new WidgetLine { WidgetId = 7, LineNo = 2, Name = "second" });
            await db.SaveChangesAsync();
        }

        await using var scope2 = app.Services.CreateAsyncScope();
        var repository = scope2.ServiceProvider.GetRequiredService<IRepository<WidgetLine>>();

        var found = await repository.GetByIdAsync([7, 2], CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("second", found.Name);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetByIdAsync([7], CancellationToken.None));
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

public static class DomainEventSignal
{
    public static List<string> Received { get; } = [];
}

public record WidgetCreated(string Name);

public class Widget : Entity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }

    public void Created() => AddDomainEvent(new WidgetCreated(Name));
}

public class WidgetCreatedHandler : IEventHandler<WidgetCreated>
{
    public Task HandleAsync(WidgetCreated @event, CancellationToken cancellationToken = default)
    {
        DomainEventSignal.Received.Add($"WidgetCreated:{@event.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>价格 &lt;= 20 的规约（按价格升序）。</summary>
public class CheapWidgetsSpecification : Specification<Widget>
{
    public CheapWidgetsSpecification()
    {
        Where(w => w.Price <= 20);
        OrderBy(w => w.Price);
    }
}

[DisableAutoDiscovery]
public class EnhancementDbContext(DbContextOptions<EnhancementDbContext> options) : DbContext(options), ILedonDbContext
{
    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<SoftWidget> SoftWidgets => Set<SoftWidget>();

    public DbSet<WidgetLine> WidgetLines => Set<WidgetLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WidgetLine>().HasKey(line => new { line.WidgetId, line.LineNo });
        modelBuilder.ApplySoftDeleteFilters();
    }
}

/// <summary>软删除夹具：验证按主键查询也尊重全局查询过滤器。</summary>
public class SoftWidget : Entity, ISoftDelete
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}

/// <summary>复合主键夹具。</summary>
public class WidgetLine
{
    public int WidgetId { get; set; }
    public int LineNo { get; set; }
    public string Name { get; set; } = string.Empty;
}
