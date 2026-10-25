namespace Ledon.Transactions;

/// <summary>
/// Saga 步骤上下文：与 <see cref="IUnitOfWork"/> 同源——步骤里照常用
/// <see cref="IUnitOfWork"/> 写业务数据 + <see cref="IUnitOfWork.AddIntegrationEvent"/>
/// 发跨服务事件，原子性与本地事务完全一致。
/// </summary>
public sealed class SagaStepContext
{
    public SagaStepContext(
        string sagaName,
        string stepName,
        IServiceProvider services,
        IDictionary<string, object?> data,
        IUnitOfWork unitOfWork)
    {
        SagaName = sagaName;
        StepName = stepName;
        Services = services;
        Data = data;
        UnitOfWork = unitOfWork;
    }

    /// <summary>Saga 名称。</summary>
    public string SagaName { get; }

    /// <summary>当前步骤名。</summary>
    public string StepName { get; }

    /// <summary>本步骤的服务提供器（独立作用域）。</summary>
    public IServiceProvider Services { get; }

    /// <summary>Saga 共享数据（跨步骤传递）。</summary>
    public IDictionary<string, object?> Data { get; }

    /// <summary>统一事务入口：步骤内的写操作与本地事务同一套 API。</summary>
    public IUnitOfWork UnitOfWork { get; }
}

/// <summary>
/// 分布式事务（Saga）构建器：一步一事务，失败时按逆序补偿已完成步骤。
/// <code>
/// await saga.For("order-fulfillment")
///     .Step("reserve", async ctx =&gt; { /* 占库存 */ },
///                     async ctx =&gt; { /* 释放库存 */ })
///     .Step("charge",  async ctx =&gt; { /* 扣款 */ },
///                     async ctx =&gt; { /* 退款 */ })
///     .RunAsync(ct);
/// </code>
/// 与 <see cref="IUnitOfWork.ExecuteAsync"/> 同一入口风格：本地事务用 ExecuteAsync，
/// 跨服务/跨模块的长流程用 Saga——补偿语义让"分布式事务"有了确定性的回滚路径。
/// </summary>
public interface ISagaTransaction
{
    /// <summary>Saga 名称（用于日志/诊断）。</summary>
    string Name { get; }

    /// <summary>Saga 共享数据。</summary>
    IDictionary<string, object?> Data { get; }

    /// <summary>
    /// 添加一个步骤。<paramref name="compensate"/> 为 null 表示该步骤不可补偿
    /// （失败时记录警告并继续补偿其余步骤）。
    /// </summary>
    ISagaTransaction Step(
        string name,
        Func<SagaStepContext, CancellationToken, Task> execute,
        Func<SagaStepContext, CancellationToken, Task>? compensate = null);

    /// <summary>
    /// 顺序执行全部步骤。任一步骤失败：已完成步骤按逆序补偿，
    /// 最终抛出 <see cref="SagaExecutionException"/>（含失败步骤与补偿结果）。
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Saga 执行失败（步骤失败且已尝试补偿）。</summary>
public sealed class SagaExecutionException : Exception
{
    public SagaExecutionException(string sagaName, string step, Exception inner)
        : base($"Saga {sagaName} 在步骤 {step} 失败（已尝试补偿已完成步骤）：{inner.Message}", inner)
    {
        SagaName = sagaName;
        Step = step;
    }

    /// <summary>失败的步骤名。</summary>
    public string Step { get; }

    /// <summary>Saga 名称。</summary>
    public string SagaName { get; }
}
