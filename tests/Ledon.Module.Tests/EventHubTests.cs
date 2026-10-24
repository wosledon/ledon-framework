using Ledon.Core.Events;
using Ledon.Module;
using Ledon.Module.Events;
using Ledon.Module.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Module.Tests;

/// <summary>
/// 事件总线错误策略：默认失败即停（v1 语义），可配置为隔离模式
/// （单个 handler 故障不波及其他订阅方，但失败不静默——全部完成后原样抛出）。
/// </summary>
public class EventHubTests
{
    [Fact]
    public async Task Handler_failure_fails_fast_by_default_and_stops_later_handlers()
    {
        Trace.Clear();

        var services = new ServiceCollection();
        services.AddLedon(o => o.DisableAutoDiscovery().AddModule<DataModule>());

        // 显式注册保证顺序确定（约定扫描的 TryAdd 不会覆盖手动注册）
        services.AddTransient<IEventHandler<IsolationEvent>, IsolationFailingHandler>();
        services.AddTransient<IEventHandler<IsolationEvent>, IsolationSucceedingHandler>();

        await using var provider = services.BuildServiceProvider();

        // 默认策略：首个 handler 失败即中断，后续 handler 不再执行，异常原样传播
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IEventBus>().PublishAsync(new IsolationEvent("x")));

        Assert.Equal("first-fails", ex.Message);
        Assert.DoesNotContain("second-ran", Trace.Snapshot());
    }

    [Fact]
    public async Task Handler_failure_isolated_mode_runs_remaining_handlers_but_does_not_swallow()
    {
        Trace.Clear();

        var services = new ServiceCollection();
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<DataModule>()
            .Events.HandlerErrorHandling = EventHandlerErrorHandling.Isolate);

        services.AddTransient<IEventHandler<IsolationEvent>, IsolationFailingHandler>();
        services.AddTransient<IEventHandler<IsolationEvent>, IsolationSucceedingHandler>();

        await using var provider = services.BuildServiceProvider();

        // 隔离模式：失败的 handler 被记录，后续 handler 照常执行
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IEventBus>().PublishAsync(new IsolationEvent("x")));

        // 但失败不静默：全部处理器执行完后，首个失败原样抛给发布方
        Assert.Equal("first-fails", ex.Message);
        Assert.Contains("second-ran", Trace.Snapshot());
    }

    [Fact]
    public async Task Isolate_mode_publishes_normally_when_no_handler_fails()
    {
        Trace.Clear();

        var services = new ServiceCollection();

        // 先手动注册（约定扫描的 TryAdd 不会覆盖手动注册，保证单一注册）
        services.AddTransient<IEventHandler<IsolationOkEvent>, IsolationOkHandler>();
        services.AddLedon(o => o
            .DisableAutoDiscovery()
            .AddModule<DataModule>()
            .Events.HandlerErrorHandling = EventHandlerErrorHandling.Isolate);

        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IEventBus>().PublishAsync(new IsolationOkEvent("ok"));

        Assert.Equal(1, Trace.Snapshot().Count(entry => entry == "ok-ran"));
    }
}

// ---------- 隔离策略夹具 ----------

public record IsolationEvent(string Name);

public record IsolationOkEvent(string Name);

/// <summary>总是失败的处理器（先注册，顺序确定）。</summary>
public class IsolationFailingHandler : IEventHandler<IsolationEvent>
{
    public Task HandleAsync(IsolationEvent @event, CancellationToken cancellationToken = default)
    {
        Trace.Add("first-ran");
        throw new InvalidOperationException("first-fails");
    }
}

/// <summary>总是成功的处理器（后注册）。</summary>
public class IsolationSucceedingHandler : IEventHandler<IsolationEvent>
{
    public Task HandleAsync(IsolationEvent @event, CancellationToken cancellationToken = default)
    {
        Trace.Add("second-ran");
        return Task.CompletedTask;
    }
}

/// <summary>专用事件的成功处理器：验证隔离模式下无故障时发布正常返回。</summary>
public class IsolationOkHandler : IEventHandler<IsolationOkEvent>
{
    public Task HandleAsync(IsolationOkEvent @event, CancellationToken cancellationToken = default)
    {
        Trace.Add("ok-ran");
        return Task.CompletedTask;
    }
}
