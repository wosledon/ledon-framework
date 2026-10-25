using Ledon.Application;
using Ledon.Caching;
using Ledon.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledon.Web.Tests;

/// <summary>CQRS 管线：分发、行为顺序、校验、事务包装。</summary>
public class ApplicationPipelineTests
{
    private static (IRequestSender Sender, Recorder Recorder) Build(
        Action<LedonApplicationOptions>? configure = null,
        IUnitOfWork? unitOfWork = null)
    {
        var recorder = new Recorder();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        services.AddLedonApplication(configure ?? (_ => { }));

        if (unitOfWork is not null)
            services.AddSingleton(unitOfWork);

        services.AddSingleton<ICommandHandler<Ping, string>, PingHandler>();
        services.AddSingleton<IQueryHandler<Sum, int>, SumHandler>();
        services.AddSingleton<ICommandHandler<Invalid, string>, InvalidHandler>();

        return (services.BuildServiceProvider().GetRequiredService<IRequestSender>(), recorder);
    }


    [Fact]
    public async Task Command_is_dispatched_to_handler()
    {
        var (sender, _) = Build();

        var reply = await sender.SendAsync(new Ping("hello"));

        Assert.Equal("Pong:hello", reply);
    }

    [Fact]
    public async Task Query_is_dispatched_to_handler()
    {
        var (sender, _) = Build();

        var sum = await sender.QueryAsync(new Sum(2, 3));

        Assert.Equal(5, sum);
    }

    [Fact]
    public async Task Behaviors_run_in_registration_order()
    {
        var (sender, recorder) = Build(o =>
        {
            o.AddBehavior(BehaviorRegistration.All<RecordingBehavior>(order: 1));
            o.AddBehavior(BehaviorRegistration.All<RecordingBehavior>(order: 2));
        });

        await sender.SendAsync(new Ping("x"));

        // 外层行为先进入、后退出；两次进入说明两个行为都生效
        Assert.Equal("enter,enter,handler,exit,exit", string.Join(',', recorder.Events));
    }

    [Fact]
    public async Task Validation_blocks_the_handler()
    {
        var (sender, recorder) = Build(o => o.AddBehavior(BehaviorRegistration.All<ValidationBehavior<Invalid>>()));

        await Assert.ThrowsAsync<ValidationException>(() => sender.SendAsync(new Invalid()).AsTask());

        Assert.DoesNotContain("handler", recorder.Events);
    }

    [Fact]
    public async Task Missing_handler_fails_fast()
    {
        var (sender, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new Unknown()).AsTask());
    }

    [Fact]
    public async Task Unregistered_request_type_fails_fast()
    {
        var services = new ServiceCollection();
        services.AddLedonApplication();
        var sender = services.BuildServiceProvider().GetRequiredService<IRequestSender>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new Ping("x")).AsTask());
    }

    [Fact]
    public async Task Transaction_behavior_wraps_the_handler()
    {
        var unitOfWork = new FakeUnitOfWork();
        var (sender, _) = Build(
            o => o.AddBehavior(BehaviorRegistration.All<TransactionBehavior<Ping>>()),
            unitOfWork);

        await sender.SendAsync(new Ping("x"));

        Assert.True(unitOfWork.WasUsed);
        Assert.True(unitOfWork.Committed);
    }

    [Fact]
    public async Task Transaction_behavior_passes_through_without_uow()
    {
        var (sender, _) = Build(o => o.AddBehavior(BehaviorRegistration.All<TransactionBehavior<Ping>>()));

        var reply = await sender.SendAsync(new Ping("x"));

        Assert.Equal("Pong:x", reply);
    }

    [Fact]
    public async Task Caching_behavior_serves_second_call_from_cache()
    {
        var calls = 0;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonApplication(o => o.AddBehavior(BehaviorRegistration.All<CachingBehavior<CachedQuery>>()));
        services.AddLedonCaching();   // 内存缓存（single-flight）
        services.AddSingleton<IQueryHandler<CachedQuery, string>, CachedQueryHandler>(_ => new CachedQueryHandler(() => calls++));

        var sender = services.BuildServiceProvider().GetRequiredService<IRequestSender>();

        var first = await sender.QueryAsync(new CachedQuery("k", TimeSpan.FromMinutes(1)));
        var second = await sender.QueryAsync(new CachedQuery("k", TimeSpan.FromMinutes(1)));

        Assert.Equal("v0", first);
        Assert.Equal("v0", second);   // 第二次来自缓存，handler 未再执行
        Assert.Equal(1, calls);
    }

    private sealed class Recorder
    {
        public List<string> Events { get; } = [];
    }

    private sealed class RecordingBehavior(Recorder recorder) : IPipelineBehavior<object>
    {
        public async ValueTask<object?> HandleAsync(object request, RequestHandlerDelegate next, CancellationToken cancellationToken = default)
        {
            recorder.Events.Add("enter");
            var response = await next().ConfigureAwait(false);
            recorder.Events.Add("exit");
            return response;
        }
    }

    public sealed record Ping(string Message) : ICommand<string>;

    private sealed class PingHandler(Recorder recorder) : ICommandHandler<Ping, string>
    {
        public Task<string> HandleAsync(Ping command, CancellationToken cancellationToken = default)
        {
            recorder.Events.Add("handler");
            return Task.FromResult($"Pong:{command.Message}");
        }
    }

    public sealed record Sum(int A, int B) : IQuery<int>;

    private sealed class SumHandler : IQueryHandler<Sum, int>
    {
        public Task<int> HandleAsync(Sum query, CancellationToken cancellationToken = default) =>
            Task.FromResult(query.A + query.B);
    }

    public sealed record Unknown : ICommand;

    public sealed record Invalid : ICommand<string>, IValidatable
    {
        public IReadOnlyCollection<string> Validate() => ["名称不能为空"];
    }

    private sealed class InvalidHandler : ICommandHandler<Invalid, string>
    {
        public Task<string> HandleAsync(Invalid command, CancellationToken cancellationToken = default) =>
            Task.FromResult("should-not-run");
    }

    public sealed record CachedQuery(string Key, TimeSpan Duration) : IQuery<string>, ICacheableQuery
    {
        public string CacheKey => Key;
        public TimeSpan CacheDuration => Duration;
    }

    private sealed class CachedQueryHandler(Func<int> counter) : IQueryHandler<CachedQuery, string>
    {
        public Task<string> HandleAsync(CachedQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult($"v{counter()}");
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public bool WasUsed { get; private set; }
        public bool Committed { get; private set; }

        public bool HasActiveTransaction => true;

        public IReadOnlyCollection<object> DomainEvents => [];

        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            WasUsed = true;
            var result = await action(cancellationToken).ConfigureAwait(false);
            Committed = true;
            return result;
        }

        public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void AddIntegrationEvent(Ledon.Queues.IIntegrationEvent @event)
        {
        }

        public void AddDomainEvent(object @event)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTenantPrefix : ICacheKeyPrefixProvider
    {
        public string? GetKeyPrefix() => null;
    }
}
