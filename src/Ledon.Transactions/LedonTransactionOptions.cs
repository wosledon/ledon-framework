namespace Ledon.Transactions;

/// <summary>统一事务配置。</summary>
public sealed class LedonTransactionOptions
{
    /// <summary>
    /// 事务隔离级别，默认 null（用数据库提供程序默认值，通常是 ReadCommitted）。
    /// </summary>
    public System.Data.IsolationLevel? IsolationLevel { get; set; }

    /// <summary>
    /// 是否在提交后分发领域事件（默认开）。
    /// 关闭后 <see cref="IUnitOfWork.DomainEvents"/> 仍会收集，但需要自行分发。
    /// </summary>
    public bool DispatchDomainEventsAfterCommit { get; set; } = true;
}
