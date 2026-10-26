using Ledon.AuditLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>审计拦截器：按 ChangeTracker 生成流水（真实 EF 管线验证）。</summary>
public class AuditLogTests : IDisposable
{
    private readonly List<string> _databases = [];

    public void Dispose()
    {
        foreach (var path in _databases)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }

        GC.SuppressFinalize(this);
    }

    private (LedonWebDbContext Context, InMemoryAuditStore Store) Build(Action<LedonAuditOptions>? configure = null)
    {
        var options = new LedonAuditOptions();
        configure?.Invoke(options);

        var store = new InMemoryAuditStore();
        var interceptor = new AuditSaveChangesInterceptor(
            store, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditSaveChangesInterceptor>.Instance);

        var path = Path.Combine(Path.GetTempPath(), $"ledon-audit-{Guid.NewGuid():N}.db");
        _databases.Add(path);

        var context = new LedonWebDbContext(
            new DbContextOptionsBuilder<LedonWebDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(interceptor)
                .Options);

        context.Database.EnsureCreated();

        return (context, store);
    }

    [Fact]
    public async Task Create_is_recorded_with_current_values()
    {
        var (context, store) = Build();

        context.Notes.Add(new AuditNote { Text = "hello" });
        await context.SaveChangesAsync();

        var entries = await store.QueryAsync();
        var entry = Assert.Single(entries);

        Assert.Equal(AuditAction.Created, entry.Action);
        Assert.Equal(nameof(AuditNote), entry.EntityType);
        Assert.Contains("hello", entry.Changes);
    }

    [Fact]
    public async Task Update_records_only_changed_properties()
    {
        var (context, store) = Build();

        var note = new AuditNote { Text = "before" };
        context.Notes.Add(note);
        await context.SaveChangesAsync();

        note.Text = "after";
        await context.SaveChangesAsync();

        var update = Assert.Single(await store.QueryAsync(), entry => entry.Action == AuditAction.Updated);
        Assert.Contains("\"before\"", update.Changes);
        Assert.Contains("\"after\"", update.Changes);
    }

    [Fact]
    public async Task No_op_save_records_nothing()
    {
        var (context, store) = Build();

        context.Notes.Add(new AuditNote { Text = "x" });
        await context.SaveChangesAsync();

        await context.SaveChangesAsync();   // 无变更

        Assert.Single(await store.QueryAsync());
    }

    [Fact]
    public async Task Delete_is_recorded()
    {
        var (context, store) = Build();

        var note = new AuditNote { Text = "bye" };
        context.Notes.Add(note);
        await context.SaveChangesAsync();

        context.Notes.Remove(note);
        await context.SaveChangesAsync();

        Assert.Contains(await store.QueryAsync(), entry => entry.Action == AuditAction.Deleted);
    }

    [Fact]
    public async Task Action_filters_are_honored()
    {
        var (context, store) = Build(o => o.AuditUpdated = false);

        var note = new AuditNote { Text = "before" };
        context.Notes.Add(note);
        await context.SaveChangesAsync();

        note.Text = "after";
        await context.SaveChangesAsync();

        Assert.DoesNotContain(await store.QueryAsync(), entry => entry.Action == AuditAction.Updated);
    }

    [Fact]
    public async Task Operator_comes_from_audit_context()
    {
        var (context, store) = Build();

        using (AuditContext.BeginScope("user-42", "alice"))
        {
            context.Notes.Add(new AuditNote { Text = "x" });
            await context.SaveChangesAsync();
        }

        var entry = Assert.Single(await store.QueryAsync());
        Assert.Equal("user-42", entry.UserId);
        Assert.Equal("alice", entry.UserName);
    }

    [Fact]
    public async Task Query_filters_by_entity_type_and_id()
    {
        var (context, store) = Build();

        context.Notes.Add(new AuditNote { Text = "a" });
        await context.SaveChangesAsync();

        var entry = Assert.Single(await store.QueryAsync());

        Assert.Single(await store.QueryAsync(entityType: nameof(AuditNote)));
        Assert.Single(await store.QueryAsync(entityId: entry.EntityId));
        Assert.Empty(await store.QueryAsync(entityType: "Other"));
    }

    [Fact]
    public async Task Purge_removes_old_entries()
    {
        var (context, store) = Build();

        context.Notes.Add(new AuditNote { Text = "a" });
        await context.SaveChangesAsync();

        var purged = await store.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(1, purged);
        Assert.Empty(await store.QueryAsync());
    }

    [Fact]
    public void Audit_store_is_registered()
    {
        var services = new ServiceCollection()
            .AddLedonAudit()
            .BuildServiceProvider();

        Assert.NotNull(services.GetRequiredService<IAuditStore>());
    }

    [Fact]
    public async Task Retention_service_purges_entries_older_than_retention()
    {
        var store = new RecordingAuditStore();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditStore>(store);
        services.AddLedonAudit(o =>
        {
            o.RetentionDays = 30;
            o.RetentionSweepInterval = TimeSpan.FromHours(1);   // 启动后立即清理一次，不必等巡检
        });

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToArray();

        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (store.PurgeCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        foreach (var service in hosted)
            await service.StopAsync(CancellationToken.None);

        Assert.True(store.PurgeCalls >= 1, "保留策略未执行清理");
        Assert.NotNull(store.LastCutoff);

        var days = (DateTimeOffset.UtcNow - store.LastCutoff!.Value).TotalDays;
        Assert.InRange(days, 29.9, 30.1);
    }

    [Fact]
    public async Task Retention_service_does_nothing_when_disabled()
    {
        var store = new RecordingAuditStore();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditStore>(store);
        services.AddLedonAudit(o => o.RetentionDays = 0);

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToArray();

        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        await Task.Delay(100);

        foreach (var service in hosted)
            await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.PurgeCalls);
    }

    private sealed class LedonWebDbContext(DbContextOptions<LedonWebDbContext> options) : DbContext(options)
    {
        public DbSet<AuditNote> Notes => Set<AuditNote>();
    }

    private sealed class AuditNote
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        public int PurgeCalls { get; private set; }

        public DateTimeOffset? LastCutoff { get; private set; }

        public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AuditEntry>> QueryAsync(
            string? entityType = null,
            string? entityId = null,
            int count = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
        {
            PurgeCalls++;
            LastCutoff = cutoffUtc;
            return Task.FromResult(0);
        }
    }
}
