namespace Ledon.Module.Events;

/// <summary>事件总线行为配置（<c>AddLedon(o =&gt; o.Events.HandlerErrorHandling = ...)</c>）。</summary>
public sealed class EventOptions
{
    /// <summary>
    /// 单个事件处理器失败时的策略，默认 <see cref="EventHandlerErrorHandling.FailFast"/>：
    /// 异常中断后续处理器并向上传播（发布方感知失败，适合事务性/强一致场景）。
    /// <see cref="EventHandlerErrorHandling.Isolate"/>：失败处理器被记录后继续执行其余处理器，
    /// 全部完成后把首个失败原样抛出——旁路订阅方（邮件/审计/通知）的故障不会波及其它订阅方，
    /// 同时失败不会静默（发布方仍会收到异常）。
    /// </summary>
    public EventHandlerErrorHandling HandlerErrorHandling { get; set; } = EventHandlerErrorHandling.FailFast;
}

/// <summary>事件处理器错误策略。</summary>
public enum EventHandlerErrorHandling
{
    /// <summary>失败即停：异常中断后续处理器并向上传播（默认）。</summary>
    FailFast = 0,

    /// <summary>隔离：记录错误后继续其余处理器，全部完成后抛出首个失败。</summary>
    Isolate = 1,
}
