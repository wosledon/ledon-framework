# Ledon.Transactions

统一事务入口：**本地事务、发件箱、Saga 分布式补偿用同一种写法**，业务代码不关心底层形态。

```csharp
public class OrderService(IUnitOfWork uow)
{
    public Task CreateAsync(CreateOrder cmd, CancellationToken ct) =>
        uow.ExecuteAsync(async token =>
        {
            var order = new Order(cmd);
            order.Create();                                  // 实体收集领域事件
            uow.AddIntegrationEvent(new OrderPlaced(order.Id)); // 跨服务事件
            await uow.SaveChangesAsync(token);
        }, ct);
}
```

- `ExecuteAsync` 自动开事务 → 保存 → 提交；异常自动回滚；
- 领域事件在**提交成功后**才分发，不会出现"事件发了但事务回滚了"；
- 嵌套 `ExecuteAsync` 加入外层事务（内层不单独提交），内层抛错整体回滚；
- `AddIntegrationEvent` 的语义由环境决定：注册发件箱时与业务数据同事务落库，未注册时提交后进程内分发；
- 跨服务长流程用 `ISagaFactory`：每步独立事务，失败按逆序补偿，最终抛 `SagaExecutionException`；
- 配置项：`LedonTransactionOptions.IsolationLevel`、`DispatchDomainEventsAfterCommit`（默认开）；
- 接入：`builder.Services.AddLedonTransactions();`

完整文档：<https://github.com/wosledon/ledon-framework>
