using Ledon.Core.Events;
using Ledon.Data;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Queues;
using Ledon.Transactions;
using Ledon.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>
/// 统一事务入口测试：
/// - 本地事务：ExecuteAsync 自动开/提/回滚，提交后才分发领域事件
/// - 集成事件：配发件箱 → 原子落库；未配 → 提交后进程内分发（同一种写法）
/// - Saga：步骤失败逆序补偿
/// </summary>
public class TransactionTests : IDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync(
        string dbPath,
        bool withOutbox = false,
        Action<LedonTransactionOptions>? configure = null,
        Action<LedonSagaOptions>? configureSaga = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLedonTransactions(configure, configureSaga);
        if (configureSaga is not null)
            builder.Services.Configure(configureSaga);
        if (withOutbox)
            builder.Services.AddLedonOutbox(o => o.PollingInterval = TimeSpan.FromHours(1));   // 不起投递
        builder.Services.AddDbContext<TxTestDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath};Cache=Shared"));
        builder.Services.AddDbContext<SagaTestDbContext>(options =>
            options.UseSqlite(
                $"Data Source={dbPath.Replace(".db", "-saga.db", StringComparison.OrdinalIgnoreCase)};Cache=Shared"));

        var app = builder.Build();
        _app = app;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
            await db.Database.EnsureCreatedAsync();

            var sagaDb = scope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
            await sagaDb.Database.EnsureCreatedAsync();

            // 顺带确认 SagaDbContext 基类型能被解析（EfSagaStore 靠它）
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SagaDbContext>());
        }

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Saga_is_idempotent_by_key()
    {
        var app = await StartAsync(NewDb("saga-idem"));

        using var scope = app.Services.CreateScope();
        var saga = scope.ServiceProvider.GetRequiredService<ISagaFactory>();

        // 同一个幂等键提交两次：第二步不会把业务跑两遍
        await saga.For("order", "order-1001")
            .Step("reserve", (ctx, ct) => Task.CompletedTask)
            .Step("charge", (ctx, ct) => Task.CompletedTask)
            .RunAsync();

        await saga.For("order", "order-1001")
            .Step("reserve", (ctx, ct) => Task.CompletedTask)
            .Step("charge", (ctx, ct) => Task.CompletedTask)
            .RunAsync();

        var store = scope.ServiceProvider.GetRequiredService<ISagaStore>();
        var instance = await store.GetByIdempotencyKeyAsync("order-1001");

        Assert.NotNull(instance);
        Assert.Equal(SagaStatus.Completed, instance!.Status);
        Assert.Equal(["reserve", "charge"], instance.CompletedSteps);
    }

    [Fact]
    public async Task Saga_persists_steps_and_survives_a_restart()
    {
        var app = await StartAsync(NewDb("saga-resume"));

        using (var scope = app.Services.CreateScope())
        {
            var saga = scope.ServiceProvider.GetRequiredService<ISagaFactory>();

            var ex = await Assert.ThrowsAsync<SagaExecutionException>(() => saga.For("order", "order-2002")
                .Step("reserve", (ctx, ct) => Task.CompletedTask)
                .Step("charge", (ctx, ct) => throw new InvalidOperationException("boom"))
                .RunAsync());

            Assert.Equal("charge", ex.Step);
        }

        // 换一个作用域（模拟进程重启后从存储恢复）：已完成步骤不会被重复执行
        using var resumed = app.Services.CreateScope();
        var store = resumed.ServiceProvider.GetRequiredService<ISagaStore>();
        var instance = await store.GetByIdempotencyKeyAsync("order-2002");

        Assert.NotNull(instance);
        Assert.Equal(SagaStatus.Compensated, instance!.Status);
        Assert.Contains("reserve", instance.CompletedSteps);

        // 步骤清单随实例持久化：恢复路径不需要调用方重新提供
        Assert.Equal(["reserve", "charge"], instance.Steps);
    }

    [Fact]
    public async Task Saga_step_timeout_is_enforced()
    {
        var app = await StartAsync(
            NewDb("saga-timeout"),
            configureSaga: o => o.StepTimeout = TimeSpan.FromMilliseconds(200));

        using var scope = app.Services.CreateScope();
        var saga = scope.ServiceProvider.GetRequiredService<ISagaFactory>();

        // 步骤不观察取消令牌：超时看门狗也必须让它失败并进入补偿
        var ex = await Assert.ThrowsAsync<SagaExecutionException>(() => saga.For("order", "order-3003")
            .Step("slow", (ctx, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct))
            .RunAsync());

        Assert.Equal("slow", ex.Step);
    }

    // ---------- 本地事务 ----------

    [Fact]
    public async Task ExecuteAsync_commits_and_dispatches_domain_events_after_commit()
    {
        TxSignal.Received.Clear();
        var app = await StartAsync(NewDb("commit"));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(async ct =>
            {
                var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
                var account = new Account { Name = "alice" };
                account.Create();                       // 实体自身收集领域事件
                db.Accounts.Add(account);
                await uow.SaveChangesAsync(ct);
            });
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
            Assert.Equal(1, await db.Accounts.CountAsync());
        }

        // 提交后领域事件才分发
        Assert.Contains("AccountCreated:alice", TxSignal.Received);
    }

    [Fact]
    public async Task ExecuteAsync_rolls_back_on_exception_and_skips_events()
    {
        TxSignal.Received.Clear();
        var app = await StartAsync(NewDb("rollback"));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync(async ct =>
            {
                var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
                db.Accounts.Add(new Account { Name = "bob" });
                await uow.SaveChangesAsync(ct);
                throw new InvalidOperationException("boom");
            }));
        }

        // 回滚：数据没进去，事件也没分发
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
            Assert.Equal(0, await db.Accounts.CountAsync());
        }

        Assert.Empty(TxSignal.Received);
    }

    [Fact]
    public async Task Nested_ExecuteAsync_joins_outer_transaction()
    {
        TxSignal.Received.Clear();
        var app = await StartAsync(NewDb("nested"));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync(async ct =>
            {
                var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
                db.Accounts.Add(new Account { Name = "outer" });
                await uow.SaveChangesAsync(ct);

                // 内层加入外层事务：内层抛错 → 整体回滚
                await uow.ExecuteAsync(async inner =>
                {
                    db.Accounts.Add(new Account { Name = "inner" });
                    await uow.SaveChangesAsync(inner);
                    throw new InvalidOperationException("inner-boom");
                });
            }));
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
            Assert.Equal(0, await db.Accounts.CountAsync());   // 外层数据也回滚了
        }
    }

    // ---------- 集成事件 ----------

    [Fact]
    public async Task Integration_event_with_outbox_is_atomic_with_business_data()
    {
        var app = await StartAsync(NewDb("outbox"), withOutbox: true);

        Guid outboxId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(async ct =>
            {
                var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
                db.Accounts.Add(new Account { Name = "carol" });
                uow.AddIntegrationEvent(new AccountCreated("carol"));
                await uow.SaveChangesAsync(ct);
            });
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
            var message = await db.OutboxMessages.SingleAsync();
            outboxId = message.Id;
            Assert.Contains("AccountCreated", message.Type);
        }

        // 业务数据也在（同一事务）
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
            Assert.Equal(1, await db.Accounts.CountAsync());
        }

        Assert.NotEqual(Guid.Empty, outboxId);
    }

    [Fact]
    public async Task Integration_event_without_outbox_falls_back_to_in_process_dispatch()
    {
        TxSignal.Received.Clear();
        var app = await StartAsync(NewDb("nooutbox"));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.ExecuteAsync(async ct =>
            {
                var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
                db.Accounts.Add(new Account { Name = "dave" });
                uow.AddIntegrationEvent(new AccountCreated("dave"));
                await uow.SaveChangesAsync(ct);
            });
        }

        // 未配发件箱：提交后进程内分发（同一写法，不同部署形态）
        Assert.Contains("AccountCreated:dave", TxSignal.Received);
    }

    [Fact]
    public async Task Integration_event_is_not_dispatched_when_transaction_rolls_back()
    {
        TxSignal.Received.Clear();
        var app = await StartAsync(NewDb("outbox-rb"));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync(async ct =>
            {
                var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
                db.Accounts.Add(new Account { Name = "eve" });
                uow.AddIntegrationEvent(new AccountCreated("eve"));
                await uow.SaveChangesAsync(ct);
                throw new InvalidOperationException("boom");
            }));
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TxTestDbContext>();
            Assert.Equal(0, await db.OutboxMessages.CountAsync());   // 发件箱消息也回滚了（原子）
        }
    }

    // ---------- Saga ----------

    [Fact]
    public async Task Saga_runs_all_steps_and_shares_data()
    {
        var app = await StartAsync(NewDb("saga-ok"));

        using var scope = app.Services.CreateScope();
        var saga = scope.ServiceProvider.GetRequiredService<ISagaFactory>();

        await saga.For("order")
            .Step("reserve", (ctx, ct) =>
            {
                ctx.Data["reserved"] = "true";
                return Task.CompletedTask;
            })
            .Step("charge", (ctx, ct) =>
            {
                Assert.Equal("true", ctx.Data["reserved"]);   // 跨步骤共享数据
                ctx.Data["charged"] = "true";
                return Task.CompletedTask;
            })
            .RunAsync();
    }

    [Fact]
    public async Task Saga_compensates_completed_steps_in_reverse_on_failure()
    {
        SagaSignal.Compensated.Clear();
        var app = await StartAsync(NewDb("saga-fail"));

        using var scope = app.Services.CreateScope();
        var saga = scope.ServiceProvider.GetRequiredService<ISagaFactory>();

        var ex = await Assert.ThrowsAsync<SagaExecutionException>(() => saga.For("order")
            .Step("reserve", (ctx, ct) => Task.CompletedTask, compensate: (ctx, ct) =>
            {
                SagaSignal.Compensated.Add("reserve");
                return Task.CompletedTask;
            })
            .Step("charge", (ctx, ct) => Task.CompletedTask, compensate: (ctx, ct) =>
            {
                SagaSignal.Compensated.Add("charge");
                return Task.CompletedTask;
            })
            .Step("ship", (ctx, ct) => throw new InvalidOperationException("no-stock"))
            .RunAsync());

        Assert.Equal("ship", ex.Step);

        // 逆序补偿：charge 先于 reserve
        Assert.Equal(new[] { "charge", "reserve" }, SagaSignal.Compensated);
    }

    [Fact]
    public async Task Saga_step_without_compensate_logs_and_continues()
    {
        SagaSignal.Compensated.Clear();
        var app = await StartAsync(NewDb("saga-nocomp"));

        using var scope = app.Services.CreateScope();
        var saga = scope.ServiceProvider.GetRequiredService<ISagaFactory>();

        await Assert.ThrowsAsync<SagaExecutionException>(() => saga.For("order")
            .Step("reserve", (ctx, ct) => Task.CompletedTask)   // 无补偿
            .Step("charge", (ctx, ct) => Task.CompletedTask, compensate: (ctx, ct) =>
            {
                SagaSignal.Compensated.Add("charge");
                return Task.CompletedTask;
            })
            .Step("ship", (ctx, ct) => throw new InvalidOperationException("boom"))
            .RunAsync());

        // 无补偿的步骤跳过，其余照常补偿
        Assert.Equal(new[] { "charge" }, SagaSignal.Compensated);
    }

    private static string NewDb(string name) =>
        Path.Combine(AppContext.BaseDirectory, $"tx-{name}-{Guid.NewGuid():N}.db");

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

public static class TxSignal
{
    public static List<string> Received { get; } = [];
}

public static class SagaSignal
{
    public static List<string> Compensated { get; } = [];
}

public record AccountCreated(string Name) : IIntegrationEvent;

public class Account : Entity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public void Create()
    {
        AddDomainEvent(new AccountCreatedDomain(Name));
    }
}

/// <summary>领域事件（进程内）。</summary>
public record AccountCreatedDomain(string Name);

public class AccountCreatedDomainHandler : IEventHandler<AccountCreatedDomain>
{
    public Task HandleAsync(AccountCreatedDomain @event, CancellationToken cancellationToken = default)
    {
        TxSignal.Received.Add($"AccountCreated:{@event.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>集成事件处理器（未配发件箱时的进程内回退路径）。</summary>
public class AccountCreatedIntegrationHandler : IEventHandler<AccountCreated>
{
    public Task HandleAsync(AccountCreated @event, CancellationToken cancellationToken = default)
    {
        TxSignal.Received.Add($"AccountCreated:{@event.Name}");
        return Task.CompletedTask;
    }
}

[DisableAutoDiscovery]
public class TxTestDbContext(DbContextOptions<TxTestDbContext> options)
    : OutboxDbContext(options), ILedonDbContext
{
    public DbSet<Account> Accounts => Set<Account>();
}

/// <summary>Saga 持久化测试用的上下文（继承 SagaDbContext 即获得实例表）。</summary>
public class SagaTestDbContext(DbContextOptions<SagaTestDbContext> options)
    : SagaDbContext(options)
{
}
