# Ledon.Application

应用层统一入口（CQRS + 管线）：处理器只写业务，验证/日志/事务/缓存由行为按顺序织入。

```csharp
builder.Services.AddLedonApplication(o =>
{
    o.UseValidation();     // IValidatable 请求先校验（失败抛 ValidationException，不进处理器）
    o.UseLogging();        // 请求类型 + 耗时 + span
    o.UseTransactions();   // 包进 IUnitOfWork（未注册事务服务时直通）
    o.UseCaching();        // ICacheableQuery 查询自动走缓存
});
```

命令/查询与处理器（约定扫描自动注册，无需手写接口约定）：

```csharp
public sealed record CreateOrder(string Sku, int Qty) : ICommand<Guid>, IValidatable
{
    public IReadOnlyCollection<string> Validate() =>
        Qty <= 0 ? ["数量必须大于 0"] : [];
}

public class CreateOrderHandler(IUnitOfWork uow) : ICommandHandler<CreateOrder, Guid>
{
    public async Task<Guid> HandleAsync(CreateOrder command, CancellationToken ct = default) { /* 只管业务 */ }
}

// 调用方（端点/控制器）
var id = await sender.SendAsync(new CreateOrder("SKU-1", 2), ct);
```

- 行为顺序 = 登记顺序（外层先执行）：验证 → 日志 → 事务 → 缓存；
- 自定义行为：实现 `IPipelineBehavior<TRequest>`，`o.AddBehavior(BehaviorRegistration.All<MyBehavior>())`；
- 事务行为与 `Ledon.Transactions` 是同一种写法（处理器里 SaveChanges，提交/回滚由行为负责）；
- 无返回值命令：`ICommand` + `ICommandHandler<TCommand>`。

完整文档：<https://github.com/wosledon/ledon-framework>
