using Ledon.Queues;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 收件箱测试：消息中间件是至少一次投递，收件箱把"重复投递"收敛为"恰好一次处理"。
/// 注册 <see cref="IInboxStore"/> 后，分发按消息标识认领（claim → 完成），
/// 重复投递直接跳过；上次处理崩溃留下的陈旧认领在超时后可重试（至少一次语义不被破坏）。
/// </summary>
public class InboxTests : IDisposable
{
    private WebApplication? _app;

    private static string NewDbPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, $"inbox-{name}-{Guid.NewGuid():N}.db");

    private async Task<WebApplication> StartAsync(string dbPath, Action<LedonInboxOptions>? configureInbox = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(50));
        builder.Services.AddLedonInbox(configureInbox);
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return app;
    }

    [Fact]
    public async Task Inbox_deduplicates_repeated_deliveries()
    {
        InboxSignal.Count = 0;

        var app = await StartAsync(NewDbPath("dedupe"));

        // 同一条投递（MessageId 相同）分发两次：handler 只执行一次
        for (var i = 0; i < 2; i++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await IntegrationEventDispatcher.DispatchAsync(
                scope.ServiceProvider, new InboxTestEvent("e1"), "msg-1", CancellationToken.None);
        }

        Assert.Equal(1, InboxSignal.Count);

        // 不同 MessageId 是不同投递，正常处理
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await IntegrationEventDispatcher.DispatchAsync(
                scope.ServiceProvider, new InboxTestEvent("e2"), "msg-2", CancellationToken.None);
        }

        Assert.Equal(2, InboxSignal.Count);

        // 去重凭据落库（已完成）
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var message = await db.InboxMessages.SingleAsync(m => m.MessageId == "msg-1");
            Assert.NotNull(message.CompletedAt);
            Assert.Equal(1, message.ClaimCount);
        }
    }

    [Fact]
    public async Task Inbox_without_store_keeps_at_least_once_semantics()
    {
        InboxSignal.Count = 0;

        // 未注册 AddLedonInbox：无 IInboxStore，分发不去重（向后兼容）
        var dbPath = NewDbPath("no-store");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(50));
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        for (var i = 0; i < 2; i++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await IntegrationEventDispatcher.DispatchAsync(
                scope.ServiceProvider, new InboxTestEvent("e1"), "msg-1", CancellationToken.None);
        }

        Assert.Equal(2, InboxSignal.Count);
    }

    [Fact]
    public async Task Inbox_claim_blocks_concurrent_processing_within_timeout()
    {
        var dbPath = NewDbPath("claim");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonInbox();
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            // 首次认领成功；未完成前（认领超时内）重复认领被拒绝 = 不会并发处理
            Assert.True(await store.TryClaimAsync("m1", "T", CancellationToken.None));
            Assert.False(await store.TryClaimAsync("m1", "T", CancellationToken.None));

            await store.MarkCompletedAsync("m1", CancellationToken.None);

            // 已完成：后续重复投递跳过
            Assert.False(await store.TryClaimAsync("m1", "T", CancellationToken.None));
        }
    }

    [Fact]
    public async Task Inbox_stale_claim_can_be_retried_after_timeout()
    {
        var dbPath = NewDbPath("stale");

        // 认领超时设为 0：模拟上次处理进程崩溃后，陈旧认领允许重新认领重试
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonInbox(o => o.ClaimTimeout = TimeSpan.Zero);
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            Assert.True(await store.TryClaimAsync("m1", "T", CancellationToken.None));
            Assert.True(await store.TryClaimAsync("m1", "T", CancellationToken.None));

            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var message = await db.InboxMessages.SingleAsync(m => m.MessageId == "m1");
            Assert.Equal(2, message.ClaimCount);   // 发生过崩溃重试
            Assert.Null(message.CompletedAt);
        }
    }

    [Fact]
    public async Task Inbox_cleanup_deletes_completed_after_retention()
    {
        var dbPath = NewDbPath("cleanup");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonInbox();
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IInboxStore>();
            Assert.True(await store.TryClaimAsync("old", "T", CancellationToken.None));
            await store.MarkCompletedAsync("old", CancellationToken.None);
            Assert.True(await store.TryClaimAsync("new", "T", CancellationToken.None));
            await store.MarkCompletedAsync("new", CancellationToken.None);
        }

        // 把 old 的完成时间改到 10 天前（超过默认 7 天保留期）
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            var old = await db.InboxMessages.SingleAsync(m => m.MessageId == "old");
            old.CompletedAt = DateTime.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IInboxStore>();
            var deleted = await store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);

            Assert.Equal(1, deleted);

            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            Assert.False(await db.InboxMessages.AnyAsync(m => m.MessageId == "old"));
            Assert.True(await db.InboxMessages.AnyAsync(m => m.MessageId == "new"));
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
