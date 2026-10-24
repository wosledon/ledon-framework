using System.Diagnostics.Metrics;
using Ledon.Queues;
using Ledon.Queues.RabbitMQ;
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

    [Fact]
    public async Task Outbox_publishes_via_transport_when_registered()
    {
        OutboxSignal.Delivered = false;

        var dbPath = Path.Combine(AppContext.BaseDirectory, $"outbox-transport-{Guid.NewGuid():N}.db");
        var transport = new RecordingTransport();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(50));
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddSingleton<IIntegrationEventTransport>(transport);

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();

            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            store.Add(new OrderPlaced("order-t1"));
            await db.SaveChangesAsync();
        }

        // 轮询等待标记完成
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

        // 注册了传输：事件走 broker，进程内 handler 不再被调用（跨服务语义）
        Assert.NotNull(message);
        Assert.NotNull(message.ProcessedAt);
        var published = transport.Published;
        Assert.True(published.Count >= 1, "事件未通过传输投递");
        var orderPlaced = Assert.IsType<OrderPlaced>(published[0]);
        Assert.Equal("order-t1", orderPlaced.OrderId);
        Assert.False(OutboxSignal.Delivered, "注册传输后进程内 handler 不应被调用");
    }

    // ---------- RabbitMQ 端到端（需 broker；设置 LEDON_TEST_RABBITMQ_URL 后启用） ----------

    [Fact]
    public async Task RabbitMQ_transport_delivers_event_to_local_handler()
    {
        var url = Environment.GetEnvironmentVariable("LEDON_TEST_RABBITMQ_URL");
        if (url is null)
            return;   // 无 broker 环境：跳过（CI/本地默认不含 RabbitMQ）

        OutboxSignal.Delivered = false;

        var dbPath = Path.Combine(AppContext.BaseDirectory, $"outbox-rabbit-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(100));
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddLedonRabbitMqTransport(o => o.ConnectionString = url);

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();

            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            store.Add(new OrderPlaced("order-rabbit"));
            await db.SaveChangesAsync();
        }

        // 全链路：Outbox → RabbitMQ → 消费侧 → 进程内 handler
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!OutboxSignal.Delivered && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.True(OutboxSignal.Delivered, "事件未经 RabbitMQ 送达进程内 handler（检查 LEDON_TEST_RABBITMQ_URL）");
    }

    // ---------- 维护：清理 / 死信 / 指标 ----------

    [Fact]
    public async Task Outbox_cleanup_deletes_only_processed_messages_beyond_retention()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"outbox-cleanup-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonOutbox(options =>
        {
            options.PollingInterval = TimeSpan.FromMilliseconds(50);
            options.CleanupInterval = TimeSpan.FromMilliseconds(100);   // 测试里让维护快速触发
        });
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        Guid processedOldId, processedNewId, pendingId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();

            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            store.Add(new OrderPlaced("old"));
            store.Add(new OrderPlaced("new"));
            store.Add(new OrderPlaced("pending"));
            await db.SaveChangesAsync();

            var messages = await db.OutboxMessages.ToListAsync();
            var processedOld = messages.Single(m => m.Payload.Contains("old"));
            var processedNew = messages.Single(m => m.Payload.Contains("new"));
            var pending = messages.Single(m => m.Payload.Contains("pending"));
            processedOldId = processedOld.Id;
            processedNewId = processedNew.Id;
            pendingId = pending.Id;

            // 直接调用存储清理（保留期 7 天）：只应删掉 10 天前处理的
            processedOld.ProcessedAt = DateTime.UtcNow.AddDays(-10);
            processedNew.ProcessedAt = DateTime.UtcNow;
            pending.ProcessedAt = null;
            await db.SaveChangesAsync();

            var deleted = await store.DeleteProcessedAsync(TimeSpan.FromDays(7), CancellationToken.None);
            Assert.Equal(1, deleted);
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            Assert.False(await db.OutboxMessages.AnyAsync(m => m.Id == processedOldId));
            Assert.True(await db.OutboxMessages.AnyAsync(m => m.Id == processedNewId));
            Assert.True(await db.OutboxMessages.AnyAsync(m => m.Id == pendingId));
        }
    }

    [Fact]
    public async Task Outbox_dead_letters_are_queryable_and_revivable()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"outbox-dead-{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // 轮询间隔拉长：避免调度器在断言窗口内把消息投递掉（本用例测的是死信查询/重放，不是投递）
        builder.Services.AddLedonOutbox(options => options.PollingInterval = TimeSpan.FromSeconds(30));
        builder.Services.AddDbContext<TestOutboxDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestOutboxDbContext>();
            await db.Database.EnsureCreatedAsync();

            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            store.Add(new OrderPlaced("dead"));
            await db.SaveChangesAsync();

            var message = await db.OutboxMessages.SingleAsync();
            message.RetryCount = 5;   // 达到 MaxRetries 默认值：搁置（死信）
            await db.SaveChangesAsync();

            // 死信：不在待投递列表里，但可查询
            var pending = await store.GetPendingAsync(10, CancellationToken.None);
            Assert.Empty(pending);

            var deadLetters = await store.GetDeadLettersAsync(10, CancellationToken.None);
            var dead = Assert.Single(deadLetters);
            Assert.Equal(message.Id, dead.Id);

            // 重放：清零重试计数后重新进入投递队列
            Assert.True(await store.ReviveAsync(dead.Id, CancellationToken.None));
            Assert.False(await store.ReviveAsync(Guid.NewGuid(), CancellationToken.None));

            var revived = await store.GetPendingAsync(10, CancellationToken.None);
            Assert.Single(revived);
            Assert.Equal(0, revived[0].RetryCount);
        }
    }

    [Fact]
    public async Task Outbox_dispatch_reports_metrics()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"outbox-metrics-{Guid.NewGuid():N}.db");

        long dispatched = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "ledon.outbox.dispatched")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref dispatched, value));
        listener.Start();

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

            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            store.Add(new OrderPlaced("metrics"));
            await db.SaveChangesAsync();
        }

        // 轮询等待指标（调度器异步投递）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref dispatched) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(dispatched >= 1, "未观测到 ledon.outbox.dispatched 指标");
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
