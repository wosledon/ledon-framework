# 统一事务入口

**用户只写一种代码，不关心本地事务还是分布式事务。**

```csharp
public class OrderService(IUnitOfWork uow)
{
    public async Task CreateAsync(CreateOrder cmd, CancellationToken ct)
    {
        await uow.ExecuteAsync(async token =>
        {
            var order = new Order(cmd);
            order.Create();                                  // 实体收集领域事件
            uow.AddIntegrationEvent(new OrderPlaced(order.Id)); // 跨服务事件
            await uow.SaveChangesAsync(token);
        }, ct);
    }
}
```

这一行里框架自动完成：开事务 → 保存 → 提交 → 分发领域事件；异常自动回滚。
**不需要**自己写 `BeginTransactionAsync` / `CommitAsync` / `try-catch-rollback`。

## 本地事务（默认）

```csharp
await uow.ExecuteAsync(async ct =>
{
    db.Orders.Add(order);
    await uow.SaveChangesAsync(ct);
}, ct);
```

- 自动 `BeginTransaction` → `SaveChanges` → `Commit`；
- 异常自动 `Rollback` 并原样抛出；
- **领域事件在提交成功后才分发**——不会出现"事件发了但事务回滚了"。

嵌套 `ExecuteAsync` 加入外层事务（内层不单独提交），内层抛错整体回滚。

## 集成事件：一种写法，两种部署形态

`uow.AddIntegrationEvent(e)` 的语义由环境决定，调用方无感：

| 环境                              | 行为                                                           |
| --------------------------------- | -------------------------------------------------------------- |
| 注册了发件箱（`AddLedonOutbox`）  | 与业务数据**同事务落库**，调度器可靠投递（可再经 RabbitMQ/Kafka 跨服务） |
| 未注册发件箱                      | 事务提交后经 `IEventBus` 进程内分发                             |

```csharp
// 单机/开发：直接进程内分发
await uow.ExecuteAsync(async ct => { uow.AddIntegrationEvent(new OrderPlaced(1)); await uow.SaveChangesAsync(ct); }, ct);

// 生产：加上一行，同样的业务代码变成"原子 + 可靠 + 可跨服务"
//   builder.Services.AddLedonOutbox();
```

## 分布式事务（Saga）

跨服务/跨模块的长流程用 Saga——每步独立事务，失败逆序补偿：

```csharp
var saga = provider.GetRequiredService<ISagaFactory>();

await saga.For("order-fulfillment")
    .Step("reserve", async (ctx, ct) => { /* 占库存 */ },
                    async (ctx, ct) => { /* 释放库存（补偿） */ })
    .Step("charge",  async (ctx, ct) => { /* 扣款 */ },
                    async (ctx, ct) => { /* 退款（补偿） */ })
    .Step("ship",   async (ctx, ct) => { /* 发货 */ },
                    async (ctx, ct) => { /* 拦截物流（补偿） */ })
    .RunAsync(ct);
```

- 每步在**独立 DI 作用域**执行，步内照常用 `ctx.UnitOfWork` 写本地数据；
- 任一步失败：已完成步骤按**逆序补偿**，最终抛 `SagaExecutionException`（含失败步骤）；
- 未提供补偿的步骤记录警告并继续补偿其余步骤（人工介入信号）；
- `ctx.Data` 跨步骤共享数据（随实例持久化，恢复后步骤仍读得到上次写进去的值）。

### 持久化与崩溃恢复

让 DbContext 继承 `SagaDbContext` 即获得实例表（与 `WorkflowDbContext` 同一风格）：

```csharp
public class OrderDbContext(DbContextOptions<OrderDbContext> options) : SagaDbContext(options);
```

之后 Saga 具备：

- **每步完成即落库**：进程在步骤之间崩溃，重启后由 `SagaRecoveryService` 认领同一个实例接着跑，
  已完成的步骤**不会重复执行**（这是崩溃恢复的关键）；
- **步骤清单随实例持久化**：恢复路径上没有构建器，实例里存的步骤名就够了；
- **幂等键**：`saga.For(name, idempotencyKey)` 相同则复用同一实例，重复提交不会把业务跑两遍；
- **单步超时**：`AddLedonTransactions(configureSaga: o => o.StepTimeout = ...)`，
  步骤不观察取消令牌也不会把 Saga 永久卡住；
- **多节点认领**：`LeasedBy` + `LeasedUntil` 保证同一实例只有一个节点在恢复；
  乐观并发令牌让两个节点同时保存时后提交的那个抛 `DbUpdateConcurrencyException`，
  而不是静默覆盖对方的 `CompletedSteps`（丢更新）。

### 步骤里发跨服务事件

```csharp
.Step("reserve", async (ctx, ct) =>
{
    await ctx.UnitOfWork.ExecuteAsync(async token =>
    {
        ctx.UnitOfWork.AddIntegrationEvent(new StockReserved(ctx.Data["orderId"] as string));
        await ctx.UnitOfWork.SaveChangesAsync(token);
    }, ct);
})
```

配了发件箱 → 该步骤的本地数据与跨服务事件原子落库；对方服务失败由它的重试/补偿处理。

## 与其它能力的关系

| 能力         | 作用                                               |
| ------------ | -------------------------------------------------- |
| 发件箱       | 跨服务可靠投递的底层（Saga/UoW 都构建在它之上）    |
| 领域事件     | 进程内解耦（提交后分发）                           |
| 工作流引擎   | 需要持久化/恢复/重试的长流程编排（Saga 的轻量替代）|
| 后台队列     | 事务提交后的异步作业                               |

**选型**：单个请求内的原子写 → `IUnitOfWork`；跨服务长流程 → Saga；
需要断点续跑/可视化 → 工作流引擎。

## 边界（明确不做）

- 2PC/XA 强一致分布式事务（需要事务管理器支持，云环境基本不可用；Saga + 补偿是务实选择）；
- 跨多个 DbContext 的单一数据库事务（需要共享连接，提供程序相关且脆弱）——用 Saga 编排多个本地事务；
- 事务传播到外部 HTTP/gRPC 调用（网络边界内没有事务）——用 Saga 步骤表达。

## 相关主题

- [数据与发件箱](~/docs/data.md)——领域事件、规约、仓储
- [可靠投递](~/docs/reliability.md)——发件箱与死信
- [工作流引擎](~/docs/workflow.md)——持久化编排
