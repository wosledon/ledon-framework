using Ledon.Queues;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 发件箱测试：事件与业务数据同事务落库 → 调度器异步投递 → 标记已处理（至少一次语义）。
/// </summary>
public class OutboxTests : IDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Outbox_dispatches_pending_events_and_marks_processed()
    {
        OutboxSignal.Delivered = false;

        var dbPath = Path.Combine(AppContext.BaseDirectory, $"outbox-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(50));
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();   // 调度器开始轮询

        // 业务事务：事件写入发件箱 + 与业务数据一次 SaveChanges
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();

            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            store.Add(new OrderPlaced("order-1"));
            await db.SaveChangesAsync();
        }

        // 轮询等待终态（投递 + 标记）——不用 TCS：handler 触发与标记完成之间存在竞态
        OutboxMessage? message = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            message = await db.OutboxMessages.FirstOrDefaultAsync();
            if (message?.ProcessedAt is not null)
                break;

            await Task.Delay(50);
        }

        // 处理器被调用过，且消息被标记为已处理（至少一次投递完成）
        Assert.True(OutboxSignal.Delivered, "集成事件处理器未被调用");
        Assert.NotNull(message);
        Assert.NotNull(message.ProcessedAt);
        Assert.Null(message.Error);
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
